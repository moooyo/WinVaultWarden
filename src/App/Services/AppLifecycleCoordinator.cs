using Core.Session;

namespace App.Services;

public enum AppLifecycleIntent
{
    Lock,
    Logout,
    CredentialChanged,
    RemoteLogout,
    Exit,
}

public interface IAppLifecycleCoordinator
{
    bool IsTransitioning { get; }
    Task RequestAsync(
        AppLifecycleIntent intent,
        VaultSessionLease? expectedSession = null);
    /// <summary>
    /// Evaluates <paramref name="predicate"/> and claims the lifecycle transition atomically
    /// with respect to <see cref="TryActivateSession"/>. Implementations must use the same
    /// synchronization boundary for both operations; a false or throwing predicate must not
    /// claim a transition.
    /// </summary>
    Task<bool> RequestIfAsync(
        AppLifecycleIntent intent,
        Func<bool> predicate);
    bool MarkSessionReady();
    bool TryActivateSession(Func<bool> activate) =>
        MarkSessionReady() && activate();
    bool TryRunSensitive(Func<bool> action) => false;
    void FailClosedSynchronously();
}

public sealed record AppLifecycleHooks(
    Action BlockSensitiveOperations,
    Action AllowSensitiveOperations,
    Action InvalidateSession,
    Func<Task> StopBackgroundAsync,
    Func<Task> CloseTransientsAsync,
    Action CloseTransientsSynchronously,
    Func<Task> ClearClipboardAsync,
    Action ClearClipboardSynchronously,
    Action ClearSensitiveState,
    Action ResetFrames,
    Func<Task> LockSessionAsync,
    Func<Task> LogoutSessionAsync,
    Action ShowLogin,
    Action ExitProcess,
    Action FailClosedSession,
    Action<string, Exception> ReportFailure,
    Func<VaultSessionLease, Task<bool>>? TryLogoutExpectedSessionAsync = null);

/// <summary>
/// Coordinates the application's only security lifecycle transition. A request atomically blocks
/// new sensitive commits, then invalidates the session without waiting for storage providers.
/// Already-authorized OS commits may finish independently while keys, background work, clipboard,
/// and sensitive views are cleared. Concurrent requests share one transition and only upgrade it.
/// </summary>
public sealed class AppLifecycleCoordinator(
    AppLifecycleHooks hooks,
    TimeSpan? exitStepTimeout = null)
    : IAppLifecycleCoordinator
{
    private static readonly TimeSpan DefaultExitStepTimeout = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly TimeSpan _exitStepTimeout = ValidateExitStepTimeout(exitStepTimeout);
    private readonly TaskCompletionSource _exitRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _transition;
    private AppLifecycleIntent _requested;
    private VaultSessionLease? _expectedSession;
    private bool _hasRequest;
    private bool _sensitiveCommitsAllowed;

    public bool IsTransitioning
    {
        get { lock (_gate) return _transition is not null; }
    }

    public Task RequestAsync(
        AppLifecycleIntent intent,
        VaultSessionLease? expectedSession = null)
    {
        lock (_gate)
            return ClaimRequestNoLock(intent, expectedSession);
    }

    public Task<bool> RequestIfAsync(
        AppLifecycleIntent intent,
        Func<bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Task? transition = null;
        Exception? predicateFailure = null;
        lock (_gate)
        {
            if (_hasRequest && Rank(_requested) == Rank(AppLifecycleIntent.Exit))
                return Task.FromResult(false);

            try
            {
                if (!predicate())
                    return Task.FromResult(false);
            }
            catch (Exception ex)
            {
                predicateFailure = ex;
            }

            if (predicateFailure is null)
                transition = ClaimRequestNoLock(intent, expectedSession: null);
        }

        if (predicateFailure is not null)
        {
            ReportSafely("request-predicate", predicateFailure);
            return Task.FromResult(false);
        }

        return AwaitConditionalRequestAsync(transition!);
    }

    public bool MarkSessionReady()
        => TryActivateSession(static () => true);

    public bool TryActivateSession(Func<bool> activate)
    {
        ArgumentNullException.ThrowIfNull(activate);
        lock (_gate)
        {
            if (_transition is not null || _hasRequest)
                return false;
            try
            {
                if (!activate())
                    return false;
            }
            catch (Exception ex)
            {
                ReportSafely("activate", ex);
                return false;
            }
            _sensitiveCommitsAllowed = true;
            Safe("allow", hooks.AllowSensitiveOperations);
            return true;
        }
    }

    public bool TryRunSensitive(Func<bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!_sensitiveCommitsAllowed || _transition is not null || _hasRequest)
                return false;

            return action();
        }
    }

    public void FailClosedSynchronously()
    {
        lock (_gate)
        {
            _sensitiveCommitsAllowed = false;
            _hasRequest = true;
        }
        Safe("block", hooks.BlockSensitiveOperations);
        Safe("invalidate", hooks.InvalidateSession);
        Safe("transients-sync", hooks.CloseTransientsSynchronously);
        Safe("clipboard-sync", hooks.ClearClipboardSynchronously);
        Safe("sensitive", hooks.ClearSensitiveState);
        Safe("frames", hooks.ResetFrames);
        Safe("session-sync", hooks.FailClosedSession);
    }

    private Task ClaimRequestNoLock(
        AppLifecycleIntent intent,
        VaultSessionLease? expectedSession)
    {
        _sensitiveCommitsAllowed = false;
        if (!_hasRequest || Rank(intent) > Rank(_requested))
        {
            _requested = intent;
            _expectedSession = expectedSession;
        }
        else if (Rank(intent) == Rank(_requested)
            && _expectedSession is not null
            && (expectedSession is null
                || !ReferenceEquals(_expectedSession, expectedSession)))
        {
            _requested = intent;
            _expectedSession = expectedSession;
        }
        _hasRequest = true;
        if (Rank(_requested) == Rank(AppLifecycleIntent.Exit))
            _exitRequested.TrySetResult();
        return _transition ??= RunAsync();
    }

    private static async Task<bool> AwaitConditionalRequestAsync(Task transition)
    {
        await transition;
        return true;
    }

    private async Task RunAsync()
    {
        await Task.Yield();

        var expectedLogoutClaimed = false;
        while (true)
        {
            AppLifecycleIntent candidateIntent;
            VaultSessionLease? candidateExpectedSession;
            lock (_gate)
            {
                candidateIntent = _requested;
                candidateExpectedSession = _expectedSession;
            }

            Safe("block", hooks.BlockSensitiveOperations);
            if (candidateExpectedSession is null)
            {
                expectedLogoutClaimed = false;
                Safe("invalidate", hooks.InvalidateSession);
                break;
            }

            var claimResult = await TryLogoutExpectedSessionAsync(
                candidateExpectedSession);
            var requestChanged = false;
            lock (_gate)
            {
                requestChanged = _requested != candidateIntent
                    || !ReferenceEquals(_expectedSession, candidateExpectedSession);
            }

            if (claimResult == ExpectedLogoutClaimResult.Claimed)
            {
                expectedLogoutClaimed = true;
                if (requestChanged)
                    continue;
                break;
            }

            if (claimResult == ExpectedLogoutClaimResult.Faulted)
            {
                // A production claim can fault only after its current-session CAS entered the
                // mutation. Finish fail-closed cleanup and retry the matching unconditional
                // session action instead of mistaking a partial cleanup failure for a stale session.
                expectedLogoutClaimed = false;
                Safe("invalidate", hooks.InvalidateSession);
                break;
            }

            lock (_gate)
            {
                if (!requestChanged)
                {
                    _transition = null;
                    _hasRequest = false;
                    _expectedSession = null;
                    _sensitiveCommitsAllowed = true;
                }
            }

            if (!requestChanged)
            {
                Safe("allow", hooks.AllowSensitiveOperations);
                return;
            }
        }

        await SafeCleanupAsync("stop", hooks.StopBackgroundAsync);
        if (await SafeCleanupAsync("transients", hooks.CloseTransientsAsync))
            Safe("transients-sync", hooks.CloseTransientsSynchronously);
        if (await SafeCleanupAsync("clipboard", hooks.ClearClipboardAsync))
            Safe("clipboard-sync", hooks.ClearClipboardSynchronously);
        Safe("sensitive", hooks.ClearSensitiveState);
        Safe("frames", hooks.ResetFrames);

        var completedRank = -1;
        while (true)
        {
            AppLifecycleIntent target;
            lock (_gate)
                target = _requested;

            var targetRank = Rank(target);
            if (targetRank > completedRank)
            {
                // Exit (rank 2) is terminal and has no additional session action.
                switch (targetRank)
                {
                    case 0:
                        await SafeAsync("lock", hooks.LockSessionAsync);
                        break;
                    case 1:
                        if (!expectedLogoutClaimed)
                            await SafeAsync("logout", hooks.LogoutSessionAsync);
                        break;
                }
                completedRank = targetRank;
                continue;
            }

            bool settled;
            lock (_gate)
            {
                settled = Rank(_requested) <= completedRank;
                if (settled)
                {
                    if (completedRank == Rank(AppLifecycleIntent.Exit))
                    {
                        // Exit is a permanent process-local latch. Keep the completed transition
                        // published so no delayed authentication or lower-rank request can reopen
                        // sensitive commits if the platform exit hook returns or fails.
                        _requested = AppLifecycleIntent.Exit;
                        _hasRequest = true;
                        _sensitiveCommitsAllowed = false;
                    }
                    else
                    {
                        _transition = null;
                        _hasRequest = false;
                    }
                    _expectedSession = null;
                }
            }
            if (settled)
                break;
        }

        if (completedRank == 2)
            Safe("exit", hooks.ExitProcess);
        else
            Safe("login", hooks.ShowLogin);
    }

    private async Task SafeAsync(string step, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ReportSafely(step, ex); }
    }

    private async Task<bool> SafeCleanupAsync(string step, Func<Task> action)
    {
        Task cleanup;
        try
        {
            cleanup = action()
                ?? throw new InvalidOperationException(
                    $"The {step} cleanup hook returned no task.");
        }
        catch (Exception exception)
        {
            ReportSafely(step, exception);
            return true;
        }

        if (!_exitRequested.Task.IsCompleted)
        {
            var completed = await Task.WhenAny(cleanup, _exitRequested.Task);
            if (ReferenceEquals(completed, cleanup))
                return await ObserveCleanupAsync(step, cleanup);
        }

        try
        {
            await cleanup.WaitAsync(_exitStepTimeout);
            return false;
        }
        catch (TimeoutException exception)
        {
            ReportSafely(step, exception);
            ObserveLateFailure(cleanup);
            return true;
        }
        catch (Exception exception)
        {
            ReportSafely(step, exception);
            return true;
        }
    }

    private async Task<bool> ObserveCleanupAsync(string step, Task cleanup)
    {
        try
        {
            await cleanup;
            return false;
        }
        catch (Exception exception)
        {
            ReportSafely(step, exception);
            return true;
        }
    }

    private static void ObserveLateFailure(Task cleanup)
    {
        _ = cleanup.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Safe(string step, Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportSafely(step, ex); }
    }

    private void ReportSafely(string step, Exception exception)
    {
        try { hooks.ReportFailure(step, exception); }
        catch { /* Diagnostic failure must not interrupt security cleanup. */ }
    }

    private async Task<ExpectedLogoutClaimResult> TryLogoutExpectedSessionAsync(
        VaultSessionLease expectedSession)
    {
        if (hooks.TryLogoutExpectedSessionAsync is null)
            return ExpectedLogoutClaimResult.Faulted;
        try
        {
            return await hooks.TryLogoutExpectedSessionAsync(expectedSession)
                ? ExpectedLogoutClaimResult.Claimed
                : ExpectedLogoutClaimResult.Stale;
        }
        catch (Exception ex)
        {
            ReportSafely("logout-expected", ex);
            return ExpectedLogoutClaimResult.Faulted;
        }
    }

    private enum ExpectedLogoutClaimResult
    {
        Stale,
        Claimed,
        Faulted,
    }

    private static int Rank(AppLifecycleIntent intent) => intent switch
    {
        AppLifecycleIntent.Exit => 2,
        AppLifecycleIntent.Logout or AppLifecycleIntent.CredentialChanged
            or AppLifecycleIntent.RemoteLogout => 1,
        _ => 0,
    };

    private static TimeSpan ValidateExitStepTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultExitStepTimeout;
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException("exitStepTimeout");
        return value;
    }
}
