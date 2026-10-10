using Core.Session;
using Api;
using Vault;

namespace App.Services;

public interface ISensitiveActionVerifier
{
    Task<bool> VerifyAsync(
        nint hwnd,
        string message,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default);
}

public interface ISensitiveActionVerificationService : ISensitiveState
{
    Task<bool> VerifyAsync(
        nint hwnd,
        string message,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default);

    TimeSpan GraceRemaining { get; }
    event EventHandler? GracePeriodChanged;
    void EndGracePeriod();
}

/// <summary>
/// Applies optional, session-bound grace to application-sensitive actions. Mandatory
/// passkey verification and item reprompts must use their own verification policies.
/// </summary>
public sealed class SensitiveActionVerificationService : ISensitiveActionVerificationService
{
    private static readonly TimeSpan GraceDuration = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly ISensitiveActionVerifier _verifier;
    private readonly VaultSession _session;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int> _readMode;
    private CancellationTokenSource _generationCancellation = new();
    private GraceState? _grace;
    private int _mode;

    public SensitiveActionVerificationService(
        ISensitiveActionVerifier verifier,
        VaultSession session,
        TimeProvider? timeProvider = null,
        Func<int>? readMode = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(session);
        _verifier = verifier;
        _session = session;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _readMode = readMode ?? (() => AppPreferences.Current.SensitiveActionVerificationIndex);
        _mode = ReadMode();
    }

    public event EventHandler? GracePeriodChanged;

    public TimeSpan GraceRemaining
    {
        get
        {
            ObserveMode();
            GraceState? grace;
            lock (_gate)
                grace = _grace;
            if (grace is null)
                return TimeSpan.Zero;

            var remaining = TimeSpan.Zero;
            var changed = false;
            // The only nested lock order is session first, service second. Lease
            // cancellation callbacks may run while VaultSession holds its lock.
            if (!_session.TryRunIfUnlocked(grace.Lease, () =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_grace, grace))
                        remaining = GetRemainingNoLock(out changed);
                }
            }))
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_grace, grace))
                    {
                        _grace = null;
                        changed = true;
                    }
                }
            }

            if (changed)
                GracePeriodChanged?.Invoke(this, EventArgs.Empty);
            return remaining;
        }
    }

    public async Task<bool> VerifyAsync(
        nint hwnd,
        string message,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (ct.IsCancellationRequested)
            return false;

        var mode = ObserveMode();
        VaultSessionLease lease;
        try
        {
            lease = _session.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        CancellationTokenSource? operationCancellation = null;
        CancellationTokenSource? generationCancellation = null;
        var reusedGrace = false;
        var changed = false;
        _session.TryRunIfUnlocked(lease, () =>
        {
            lock (_gate)
            {
                if (ct.IsCancellationRequested || mode != _mode)
                    return;

                if (mode == 1 && _grace?.Lease.SameSessionAs(lease) == true)
                    reusedGrace = GetRemainingNoLock(out changed) > TimeSpan.Zero;
                else if (_grace is not null)
                {
                    _grace = null;
                    changed = true;
                }

                generationCancellation = _generationCancellation;
                operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    ct, lease.CancellationToken, generationCancellation.Token);
            }
        });

        if (changed)
            GracePeriodChanged?.Invoke(this, EventArgs.Empty);
        if (operationCancellation is null)
            return false;

        using (operationCancellation)
        {
            var operationToken = operationCancellation.Token;
            if (ct.IsCancellationRequested || operationToken.IsCancellationRequested)
                return false;

            if (!reusedGrace)
            {
                bool verified;
                try
                {
                    var verification = _verifier.VerifyAsync(hwnd, message, prompt, operationToken);
                    verified = await AwaitVerificationAsync(verification, operationToken);
                }
                catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex, operationToken))
                {
                    return false;
                }
                catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
                {
                    return false;
                }
                catch (Exception) when (ct.IsCancellationRequested || operationToken.IsCancellationRequested)
                {
                    return false;
                }

                if (!verified)
                    return false;
            }

            if (ct.IsCancellationRequested || operationToken.IsCancellationRequested || ObserveMode() != mode)
                return false;

            var accepted = false;
            GraceState? createdGrace = null;
            changed = false;
            _session.TryRunIfUnlocked(lease, () =>
            {
                lock (_gate)
                {
                    if (ct.IsCancellationRequested || operationToken.IsCancellationRequested || _mode != mode
                        || !ReferenceEquals(_generationCancellation, generationCancellation))
                        return;

                    if (reusedGrace)
                    {
                        accepted = _grace?.Lease.SameSessionAs(lease) == true
                            && GetRemainingNoLock(out changed) > TimeSpan.Zero;
                        return;
                    }

                    accepted = true;
                    if (mode == 1)
                    {
                        _grace = createdGrace = new GraceState(lease, _timeProvider.GetTimestamp());
                        changed = true;
                    }
                }
            });

            if (changed)
                GracePeriodChanged?.Invoke(this, EventArgs.Empty);
            if (!ct.IsCancellationRequested && !operationToken.IsCancellationRequested)
                return accepted;

            // A cancellation after the commit (including a notification handler)
            // must not leave this unsuccessful request's new approval reusable.
            changed = false;
            lock (_gate)
            {
                if (createdGrace is not null && ReferenceEquals(_grace, createdGrace))
                {
                    _grace = null;
                    changed = true;
                }
            }
            if (changed)
                GracePeriodChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
    }

    public void EndGracePeriod()
    {
        CancellationTokenSource previous;
        lock (_gate)
        {
            previous = _generationCancellation;
            _generationCancellation = new CancellationTokenSource();
            _grace = null;
        }

        CancelGeneration(previous);
        GracePeriodChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSensitive() => EndGracePeriod();

    private int ObserveMode()
    {
        var mode = ReadMode();
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            if (mode != _mode)
            {
                _mode = mode;
                previous = _generationCancellation;
                _generationCancellation = new CancellationTokenSource();
                _grace = null;
            }
        }

        if (previous is not null)
        {
            CancelGeneration(previous);
            GracePeriodChanged?.Invoke(this, EventArgs.Empty);
        }
        return mode;
    }

    private int ReadMode() => _readMode() == 1 ? 1 : 0;

    private TimeSpan GetRemainingNoLock(out bool changed)
    {
        changed = false;
        if (_grace is null)
            return TimeSpan.Zero;

        var elapsed = _timeProvider.GetElapsedTime(_grace.StartedAt, _timeProvider.GetTimestamp());
        if (_mode == 1 && !_grace.Lease.CancellationToken.IsCancellationRequested
            && elapsed >= TimeSpan.Zero && elapsed < GraceDuration)
            return GraceDuration - elapsed;

        _grace = null;
        changed = true;
        return TimeSpan.Zero;
    }

    private static void CancelGeneration(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // A failed verifier cancellation callback must not prevent revocation.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static async Task<bool> AwaitVerificationAsync(Task<bool> verification, CancellationToken ct)
    {
        try
        {
            return await verification.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A native prompt may ignore cancellation. Always observe its eventual
            // completion, including when cancellation preceded the first await.
            _ = ObserveCompletionAsync(verification);
            throw;
        }
    }

    private static async Task ObserveCompletionAsync(Task<bool> verification)
    {
        try
        {
            await verification.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The cancelled request no longer owns an authorization result.
        }
    }

    private sealed record GraceState(VaultSessionLease Lease, long StartedAt);
}
