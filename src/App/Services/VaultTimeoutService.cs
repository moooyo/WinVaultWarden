using Core.Session;

namespace App.Services;

public sealed class VaultTimeoutService
{
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _lastActivity;
    private bool _armed;
    private static readonly TimeSpan ActivityThrottle = TimeSpan.FromSeconds(2);

    public VaultTimeoutService(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public event EventHandler<VaultTimeoutAction>? TimeoutRequested;
    public DateTimeOffset LastActivity => _lastActivity;
    public bool IsArmed => _armed;

    public void Start() { _lastActivity = _now(); _armed = true; }
    public void Stop() => _armed = false;

    public void NotifyActivity()
    {
        if (!_armed) return;
        var t = _now();
        if (t - _lastActivity >= ActivityThrottle) _lastActivity = t;
    }

    public void Tick(
        IVaultSnapshot snapshot,
        int sessionTimeoutIndex,
        int timeoutActionIndex)
    {
        if (!_armed) return;
        var timeout = IdleTimeoutPolicy.Resolve(
            snapshot,
            sessionTimeoutIndex,
            timeoutActionIndex);
        if (IdleTimeoutPolicy.IsExpired(_lastActivity, _now(), timeout))
        {
            _armed = false;
            TimeoutRequested?.Invoke(this, timeout.Action);
        }
    }

    public void HandleSystemLock(
        IVaultSnapshot snapshot,
        int sessionTimeoutIndex,
        int timeoutActionIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_armed) return;

        var timeout = IdleTimeoutPolicy.Resolve(
            snapshot,
            sessionTimeoutIndex,
            timeoutActionIndex);
        _armed = false;
        TimeoutRequested?.Invoke(
            this,
            timeout.Trigger == VaultTimeoutTrigger.OnSystemLock
                ? timeout.Action
                : VaultTimeoutAction.Lock);
    }

    public void LockNow()
    {
        if (!_armed) return;
        _armed = false;
        TimeoutRequested?.Invoke(this, VaultTimeoutAction.Lock);
    }
}
