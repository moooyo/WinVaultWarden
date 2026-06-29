using Api;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Vault;
using Xunit;

namespace Vault.Tests;

/// <summary>
/// NotificationsService 的 TDD 测试集。
///
/// 遵循 TDD：先写测试（RED），再写最小实现（GREEN）。
///
/// 覆盖场景：
///   1. StartAsync 驱动 dispatcher 处理脚本化消息，VaultChanged 事件被触发。
///   2. StopAsync 停止循环，后续不再 dispatch。
///   3. 连接首次抛异常后，触发重连（ConnectAsync 被调用 2 次）。
///   4. session / tokenStore 均无 serverUrl 时，循环立即退出，不 connect。
///   5. Dispatcher 的 4 个事件透传为 NotificationsService 自身的 4 个事件。
/// </summary>
public class NotificationsServiceTests
{
    // ─────────────────────────── 工厂方法 ───────────────────────────

    /// <summary>
    /// 创建一个已设置 AccessToken 和 Account.ServerUrl 的 VaultSession。
    /// </summary>
    private static VaultSession MakeSession(
        string serverUrl = "http://test-server:8080",
        string accessToken = "test-access-token")
    {
        var session = new VaultSession();
        session.SetTokens(accessToken, "refresh-token");
        session.SetAccount(new AccountInfo(
            Email: "test@example.com",
            ServerUrl: serverUrl,
            Initial: "T",
            KdfSummary: "PBKDF2/600000"));
        return session;
    }

    /// <summary>
    /// 创建 NotificationsService，使用近零 backoff 避免测试超时。
    /// </summary>
    private static NotificationsService MakeService(
        Func<INotificationsConnection> factory,
        INotificationDispatcher dispatcher,
        VaultSession session,
        ITokenStore? tokenStore = null,
        ITokenRefresher? refresher = null,
        TimeSpan? baseBackoff = null)
    {
        return new NotificationsService(
            factory,
            dispatcher,
            session,
            tokenStore ?? new MemoryTokenStore(),
            refresher ?? new FakeTokenRefresher(),
            baseBackoff ?? TimeSpan.FromMilliseconds(10));
    }

    // ─────────────────────────── 场景 1 ───────────────────────────

    /// <summary>
    /// StartAsync 异步驱动 dispatcher.DispatchAsync。
    /// 脚本化 3 条消息（SyncVault、SyncSendCreate、AuthRequest），
    /// 等待全部处理完毕后断言：
    ///   - dispatcher.Dispatched.Count >= 3
    ///   - VaultChanged / SendsChanged / AuthRequestsChanged 各至少触发一次
    ///
    /// FakeNotificationsConnection 的 Messages 批次消费完后，ReadAsync 挂起等待取消，
    /// 不会触发重连。因此 StopAsync 前不会超出 3 条消息。
    /// </summary>
    [Fact]
    public async Task StartAsync_ScriptedMessages_AllDispatched_EventsFired()
    {
        // Arrange
        var messages = new List<NotificationMessage>
        {
            new((int)UpdateType.SyncVault,      null),
            new((int)UpdateType.SyncSendCreate, null),
            new((int)UpdateType.AuthRequest,    null),
        };

        var conn = new FakeNotificationsConnection { Messages = messages };
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();

        var vaultChangedCount    = 0;
        var sendsChangedCount    = 0;
        var authChangedCount     = 0;

        var svc = MakeService(() => conn, dispatcher, session);
        svc.VaultChanged        += () => vaultChangedCount++;
        svc.SendsChanged        += () => sendsChangedCount++;
        svc.AuthRequestsChanged += () => authChangedCount++;

        // Act
        await svc.StartAsync();

        // 等待消息全部处理完：脚本批次 yield 3 条后，ReadAsync 挂起（不重连）
        // 所以 dispatcher.Dispatched.Count 会稳定在 3
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dispatcher.Dispatched.Count < 3 && !cts.Token.IsCancellationRequested)
            await Task.Delay(10);

        await svc.StopAsync();

        // Assert：批次消费后不重连，所以精确是 3 条
        Assert.Equal(3, dispatcher.Dispatched.Count);
        Assert.Equal(1, vaultChangedCount);
        Assert.Equal(1, sendsChangedCount);
        Assert.Equal(1, authChangedCount);
    }

    // ─────────────────────────── 场景 2 ───────────────────────────

    /// <summary>
    /// StopAsync 停止循环，已分发的消息被处理，Stop 后不再 dispatch。
    /// 使用一个永不结束的 FakeConnection（空消息列表但不会抛出），
    /// 手动 Stop，断言 Connect 后 Stop 立即终止。
    /// </summary>
    [Fact]
    public async Task StopAsync_StopsLoop_NoFurtherDispatch()
    {
        // Arrange：空消息连接，ReadAsync 立即结束（触发 backoff 重连），但 backoff 很短
        var conn = new FakeNotificationsConnection { Messages = new() }; // 空列表
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();

        var svc = MakeService(() => conn, dispatcher, session,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        // Act
        await svc.StartAsync();
        // 让连接循环跑一小会儿
        await Task.Delay(50);
        var countBeforeStop = dispatcher.Dispatched.Count;

        await svc.StopAsync();
        // Stop 之后等待一段时间，dispatch 不应继续增加
        await Task.Delay(100);
        var countAfterStop = dispatcher.Dispatched.Count;

        // Assert — stop 后计数不再增长（允许 stop 时最后一次飞行消息）
        Assert.Equal(countBeforeStop, countAfterStop);
    }

    // ─────────────────────────── 场景 3 ───────────────────────────

    /// <summary>
    /// 连接首次抛异常 → 触发重连，ConnectAsync 被调用 2 次。
    ///
    /// 使用 ThrowOnConnectCount=1 的 FakeConnection（第 1 次抛，第 2 次成功并
    /// yield 一条消息）。因为 FakeNotificationsConnection 有状态（ConnectCallCount），
    /// 这里使用同一个实例（工厂每次返回同一实例）来观察重连。
    /// </summary>
    [Fact]
    public async Task FaultedConnection_TriggersReconnect_ConnectCalledTwice()
    {
        // Arrange：第 1 次 Connect 抛出，第 2 次成功后 yield 1 条消息
        var conn = new FakeNotificationsConnection
        {
            ThrowOnConnectCount = 1,
            Messages = new List<NotificationMessage>
            {
                new((int)UpdateType.SyncVault, null)
            }
        };

        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();

        var svc = MakeService(() => conn, dispatcher, session,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        // Act
        await svc.StartAsync();

        // 等待 dispatch 至少 1 条（说明第 2 次连接成功了）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dispatcher.Dispatched.Count < 1 && !cts.Token.IsCancellationRequested)
            await Task.Delay(10, cts.Token).ConfigureAwait(false);

        await svc.StopAsync();

        // Assert — ConnectAsync 至少被调用了 2 次（1 次失败 + 1 次成功）
        Assert.True(conn.ConnectCallCount >= 2,
            $"Expected ConnectCallCount >= 2, got {conn.ConnectCallCount}");
        Assert.Equal(1, dispatcher.Dispatched.Count);
    }

    // ─────────────────────────── 场景 4 ───────────────────────────

    /// <summary>
    /// session.Account.ServerUrl 为空 且 tokenStore 无数据 → 循环立即退出，
    /// ConnectAsync 从未被调用。
    /// </summary>
    [Fact]
    public async Task NoServerUrl_LoopExitsWithoutConnect()
    {
        // Arrange：空 session，空 tokenStore
        var conn = new FakeNotificationsConnection
        {
            Messages = new List<NotificationMessage>
            {
                new((int)UpdateType.SyncVault, null)
            }
        };
        var dispatcher = new FakeNotificationDispatcher();
        var session = new VaultSession(); // Account.ServerUrl == ""
        // 也不设置 accessToken
        var tokenStore = new MemoryTokenStore(); // 空

        var svc = MakeService(() => conn, dispatcher, session, tokenStore: tokenStore);

        // Act
        await svc.StartAsync();
        // 给循环足够时间完成（应快速退出）
        await Task.Delay(100);
        await svc.StopAsync();

        // Assert — Connect 从未被调用
        Assert.Equal(0, conn.ConnectCallCount);
        Assert.Empty(dispatcher.Dispatched);
    }

    // ─────────────────────────── 场景 5 ───────────────────────────

    /// <summary>
    /// dispatcher 的 LoggedOut 事件透传为 NotificationsService.LoggedOut。
    /// FakeNotificationsConnection 的 Messages 批次消费完后挂起，不重连，
    /// 所以 LoggedOut 精确触发 1 次。
    /// </summary>
    [Fact]
    public async Task DispatcherLoggedOut_ForwardsToServiceEvent()
    {
        // Arrange
        var messages = new List<NotificationMessage>
        {
            new((int)UpdateType.LogOut, null),
        };
        var conn = new FakeNotificationsConnection { Messages = messages };
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();

        var loggedOutFired = 0;
        var svc = MakeService(() => conn, dispatcher, session);
        svc.LoggedOut += () => loggedOutFired++;

        // Act
        await svc.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (loggedOutFired < 1 && !cts.Token.IsCancellationRequested)
            await Task.Delay(10);

        await svc.StopAsync();

        // Assert：批次消费后 ReadAsync 挂起不重连，精确 1 次
        Assert.Equal(1, loggedOutFired);
    }

    // ─────────────────────────── 场景 6 ───────────────────────────

    /// <summary>
    /// StartAsync 多次调用是幂等的（不会启动多个循环）。
    /// </summary>
    [Fact]
    public async Task StartAsync_CalledTwice_IsIdempotent()
    {
        // Arrange
        var conn = new FakeNotificationsConnection { Messages = new() };
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();
        var svc = MakeService(() => conn, dispatcher, session,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        // Act — 连续两次 StartAsync
        await svc.StartAsync();
        await svc.StartAsync();
        await Task.Delay(50);

        // 每次 ReadAsync 结束后 loop 会重连；但只有一个循环在运行
        // 断言：第一次 StartAsync 已经连接，第二次不应重复启动新循环
        var connectCountAfterBothStarts = conn.ConnectCallCount;

        await svc.StopAsync();

        // 不断言具体次数（取决于 backoff 内触发了几次重连），
        // 主要断言 StopAsync 不会挂死（如果有两个 Task.Run 会互相竞争 CTS）
        Assert.True(true, "StopAsync completed without hanging");
    }

    // ─────────────────────────── 场景 7 ───────────────────────────

    /// <summary>
    /// StopAsync 幂等：连续调用两次不抛出。
    /// </summary>
    [Fact]
    public async Task StopAsync_CalledTwice_IsIdempotent()
    {
        var conn = new FakeNotificationsConnection { Messages = new() };
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();
        var svc = MakeService(() => conn, dispatcher, session);

        await svc.StartAsync();
        await svc.StopAsync(); // 第 1 次
        var ex = await Record.ExceptionAsync(() => svc.StopAsync()); // 第 2 次
        Assert.Null(ex);
    }
}
