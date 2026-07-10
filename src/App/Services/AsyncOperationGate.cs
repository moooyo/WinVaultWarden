namespace App.Services;

/// <summary>
/// 单持有者原子闸门:同一时刻只允许一个调用者持有租约,Dispose 后才能重新进入。
/// 用于避免同一异步操作(如登录/同步)被并发重入。
/// </summary>
public sealed class AsyncOperationGate
{
    private int _held;

    public bool TryEnter(out IDisposable? lease)
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            lease = null;
            return false;
        }

        lease = new Releaser(this);
        return true;
    }

    private sealed class Releaser(AsyncOperationGate owner) : IDisposable
    {
        private AsyncOperationGate? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
                Volatile.Write(ref current._held, 0);
        }
    }
}
