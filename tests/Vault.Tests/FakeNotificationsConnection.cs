using Api;
using Core.Models;

namespace Vault.Tests;

/// <summary>
/// INotificationsConnection 的测试替身。
///
/// 行为：
/// - ConnectAsync：如果 <see cref="ThrowOnConnectCount"/> &gt; 0，则前 N 次调用抛出
///   <see cref="InvalidOperationException"/>，之后正常返回。
/// - ReadAsync：每次调用按顺序从 <see cref="MessageQueues"/> 取出下一批消息 yield，
///   批次用完后该次 ReadAsync 正常结束。若队列已空，则挂起等待取消（模拟长连接）。
///   简便构造：用 <see cref="Messages"/> 属性设置单批消息（仅首次 ReadAsync yield，之后挂起）。
/// - DisposeAsync：幂等，不抛出。
///
/// 测试可通过 <see cref="ConnectCallCount"/> 断言重连次数。
/// </summary>
public sealed class FakeNotificationsConnection : INotificationsConnection
{
    /// <summary>
    /// 多批脚本消息。每次 ConnectAsync 成功后的 ReadAsync 依次消费一批，
    /// 批次消费完则本次 ReadAsync 正常结束（触发重连）。
    /// 所有批次用完后，ReadAsync 挂起等待取消（模拟永久连接）。
    /// </summary>
    public Queue<List<NotificationMessage>> MessageQueues { get; } = new();

    /// <summary>
    /// 快捷设置：单批消息（仅首次成功 ReadAsync yield，之后挂起）。
    /// 与 <see cref="MessageQueues"/> 互斥使用；设置后会清空 MessageQueues 并重新入队。
    /// </summary>
    public List<NotificationMessage> Messages
    {
        init
        {
            MessageQueues.Clear();
            MessageQueues.Enqueue(value);
        }
    }

    /// <summary>前 N 次 ConnectAsync 调用将抛出异常，之后正常。0 表示从不抛出。</summary>
    public int ThrowOnConnectCount { get; init; }

    /// <summary>记录 ConnectAsync 被调用的次数（包含失败次数）。</summary>
    public int ConnectCallCount { get; private set; }

    /// <summary>记录最近一次 ConnectAsync 收到的 accessToken。</summary>
    public string? LastAccessToken { get; private set; }

    public Task ConnectAsync(string serverUrl, string accessToken, CancellationToken ct)
    {
        ConnectCallCount++;
        LastAccessToken = accessToken;

        if (ConnectCallCount <= ThrowOnConnectCount)
            throw new InvalidOperationException($"Fake connect failure #{ConnectCallCount}");

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<NotificationMessage> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 取下一批消息（若有）
        List<NotificationMessage>? batch = null;
        lock (MessageQueues)
        {
            if (MessageQueues.Count > 0)
                batch = MessageQueues.Dequeue();
        }

        if (batch is not null)
        {
            // 有脚本消息：逐条 yield 后正常结束（模拟连接关闭 → 触发重连）
            foreach (var msg in batch)
            {
                ct.ThrowIfCancellationRequested();
                yield return msg;
                await Task.Yield();
            }
        }
        else
        {
            // 无更多批次：挂起等待取消（模拟长连接，不触发重连）
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Func&lt;INotificationsConnection&gt; 工厂的辅助包装，
/// 允许测试注入一个按顺序返回不同连接实例的工厂。
/// </summary>
public sealed class SequentialConnectionFactory
{
    private readonly Queue<INotificationsConnection> _queue;

    public SequentialConnectionFactory(params INotificationsConnection[] connections)
    {
        _queue = new Queue<INotificationsConnection>(connections);
    }

    public int TotalCreated { get; private set; }

    public INotificationsConnection Create()
    {
        TotalCreated++;
        // 如果已用完，重用最后一个（防止无限重连测试死循环）
        return _queue.Count > 1 ? _queue.Dequeue() : _queue.Peek();
    }
}

/// <summary>
/// INotificationDispatcher 的最小化测试替身：
/// 记录所有 DispatchAsync 调用，并转发同名事件。
/// </summary>
public sealed class FakeNotificationDispatcher : Vault.INotificationDispatcher
{
    public List<NotificationMessage> Dispatched { get; } = new();

    public event Action? VaultChanged;
    public event Action? SendsChanged;
    public event Action? AuthRequestsChanged;
    public event Action? LoggedOut;

    public Task DispatchAsync(NotificationMessage msg, CancellationToken ct)
    {
        Dispatched.Add(msg);

        // 根据消息类型触发对应事件（简化：仅按字段 Type 判断）
        var type = (Core.Enums.UpdateType)msg.Type;
        switch (type)
        {
            case Core.Enums.UpdateType.LogOut:
                LoggedOut?.Invoke();
                break;
            case Core.Enums.UpdateType.SyncSendCreate:
            case Core.Enums.UpdateType.SyncSendUpdate:
            case Core.Enums.UpdateType.SyncSendDelete:
                SendsChanged?.Invoke();
                break;
            case Core.Enums.UpdateType.AuthRequest:
                AuthRequestsChanged?.Invoke();
                break;
            default:
                VaultChanged?.Invoke();
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>触发 VaultChanged（供测试外部驱动）。</summary>
    public void RaiseVaultChanged() => VaultChanged?.Invoke();
}

/// <summary>
/// INotificationsConnection 的辅助替身：ConnectAsync 先执行 onConnect 回调，
/// 然后挂起等待 blockUntil 完成（或取消）。用于幂等测试，精确计数并发 Connect 调用。
/// </summary>
public sealed class BlockingFakeConnection : INotificationsConnection
{
    private readonly Action _onConnect;
    private readonly Task _blockUntil;

    public BlockingFakeConnection(Action onConnect, Task blockUntil)
    {
        _onConnect  = onConnect;
        _blockUntil = blockUntil;
    }

    public async Task ConnectAsync(string serverUrl, string accessToken, CancellationToken ct)
    {
        _onConnect();
        // 挂起：等 blockUntil 完成，或外部取消
        await Task.WhenAny(_blockUntil, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    public async IAsyncEnumerable<NotificationMessage> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 不产生消息，挂起等取消
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// ITokenRefresher 的最小化测试替身：始终返回指定结果（默认 true）。
/// </summary>
public sealed class FakeTokenRefresher : Core.Services.ITokenRefresher
{
    public bool Result { get; init; } = true;
    public int CallCount { get; private set; }

    public Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(Result);
    }
}
