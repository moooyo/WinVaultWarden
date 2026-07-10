namespace App.Services;

/// <summary>敏感状态持有者:必须能在锁屏/登出等生命周期事件中清空自身内存中的机密。</summary>
public interface ISensitiveState
{
    void ClearSensitive();
}

public interface ISensitiveStateRegistry
{
    void Register(ISensitiveState state);
    void ClearAll();
}

/// <summary>
/// 以弱引用跟踪所有已注册的敏感状态持有者,ClearAll 时逐个调用 ClearSensitive。
/// 单个条目抛出的异常会被上报但不阻断其余条目清理;回调始终在内部锁之外执行,
/// 避免持锁期间发生重入死锁或长时间阻塞其他线程的 Register/ClearAll 调用。
/// </summary>
public sealed class SensitiveStateRegistry(Action<Exception> reportFailure) : ISensitiveStateRegistry
{
    private readonly object _gate = new();
    private readonly List<WeakReference<ISensitiveState>> _states = [];

    public void Register(ISensitiveState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
            _states.Add(new WeakReference<ISensitiveState>(state));
    }

    public void ClearAll()
    {
        WeakReference<ISensitiveState>[] snapshot;
        lock (_gate)
        {
            _states.RemoveAll(reference => !reference.TryGetTarget(out _));
            snapshot = [.. _states];
        }

        foreach (var reference in snapshot)
        {
            if (!reference.TryGetTarget(out var state))
                continue;
            try { state.ClearSensitive(); }
            catch (Exception ex) { reportFailure(ex); }
        }
    }
}
