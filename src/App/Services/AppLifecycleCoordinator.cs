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
public sealed class AppLifecycleCoordinator(AppLifecycleHooks hooks)
    : IAppLifecycleCoordinator
{
    private readonly object _gate = new();
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
            return _transition ??= RunAsync();
        }
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
                // mutation. Finish fail-closed cleanup and retry the unconditional logout later
                // instead of mistaking a partial cleanup failure for a stale session.
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

        await SafeAsync("stop", hooks.StopBackgroundAsync);
        await SafeAsync("transients", hooks.CloseTransientsAsync);
        await SafeAsync("clipboard", hooks.ClearClipboardAsync);
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
                // Exit(rank 2) 是终态:没有对应的会话动作,清理完毕即退出进程。
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
                    _transition = null;
                    _hasRequest = false;
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

    private void Safe(string step, Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportSafely(step, ex); }
    }

    private void ReportSafely(string step, Exception exception)
    {
        try { hooks.ReportFailure(step, exception); }
        catch { /* 诊断失败不得中断安全清理。 */ }
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
}
