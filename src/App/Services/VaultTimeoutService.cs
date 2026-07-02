namespace App.Services;

// 空闲/最小化/系统锁屏 → 决策并抛 TimeoutRequested;不依赖 WinUI/auth,由 MainWindow 执行动作。
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

    public void Tick(int minutes, VaultTimeoutAction idleAction)
    {
        if (!_armed) return;
        if (IdleTimeoutPolicy.IsExpired(_lastActivity, _now(), minutes))
        {
            _armed = false;
            TimeoutRequested?.Invoke(this, idleAction);
        }
    }

    public void LockNow()
    {
        if (!_armed) return;
        _armed = false;
        TimeoutRequested?.Invoke(this, VaultTimeoutAction.Lock);
    }
}
