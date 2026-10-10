namespace App.Services;

/// <summary>
/// 跟踪正在打开的瞬态操作(对话框/弹窗等),使生命周期协调器能在锁屏/登出前统一取消并关闭它们。
/// </summary>
public interface ITransientOperationRegistry
{
    IDisposable Register(Action cancel, Action close, Action clearSensitive);
    Task CancelAndCloseAllAsync();
    void CancelAndCloseAllSynchronously();
}

/// <summary>
/// 注册表本身不持有强引用之外的生命周期语义:Register 返回的 IDisposable 仅用于
/// "该瞬态操作已自行正常结束"时从表中移除自身,不会触发 cancel/close/clearSensitive
/// (这三者只在 CancelAndCloseAll* 强制清理路径中被调用)。Dispose 可重复调用,幂等。
/// 回调始终在内部锁之外执行,避免持锁期间重入死锁。
/// </summary>
public sealed class TransientOperationRegistry(Action<Exception> reportFailure)
    : ITransientOperationRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<Entry> _entries = [];


    public IDisposable Register(Action cancel, Action close, Action clearSensitive)
    {
        var entry = new Entry(this, cancel, close, clearSensitive);
        lock (_gate)
            _entries.Add(entry);
        return entry;
    }

    public Task CancelAndCloseAllAsync()
    {
        CancelAndCloseAllSynchronously();
        return Task.CompletedTask;
    }

    public void CancelAndCloseAllSynchronously()
    {
        Entry[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries];
            _entries.Clear();
        }

        foreach (var entry in snapshot)
        {
            Invoke(entry.Cancel);
            Invoke(entry.Close);
            Invoke(entry.ClearSensitive);
            entry.Detach();
        }
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
            _entries.Remove(entry);
    }

    private void Invoke(Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportSafely(ex); }
    }

    private void ReportSafely(Exception exception)
    {
        try { reportFailure(exception); }
        catch { /* 诊断失败不得中断安全清理。 */ }
    }

    private sealed class Entry(
        TransientOperationRegistry owner,
        Action cancel,
        Action close,
        Action clearSensitive) : IDisposable
    {
        private TransientOperationRegistry? _owner = owner;
        internal Action Cancel { get; } = cancel;
        internal Action Close { get; } = close;
        internal Action ClearSensitive { get; } = clearSensitive;
        internal void Detach() => Interlocked.Exchange(ref _owner, null);
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(this);
    }
}
