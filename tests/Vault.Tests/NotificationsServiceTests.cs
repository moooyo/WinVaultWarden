using Api;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using System.Threading;
using Vault;
using Xunit;

namespace Vault.Tests;

/// <summary>
/// NotificationsService 的 TDD 测试集。
///
/// Task 9：StartAsync 只捕获一次租约；整个循环——连接、刷新、分发——复用该租约；
/// 账户切换/锁定/登出取消租约令牌 → 循环退出，旧连接绝不看到新账户的 origin/token。
///
/// 覆盖场景：
///   1. StartAsync 驱动 dispatcher 处理脚本化消息，事件被触发。
///   2. StopAsync 停止循环，后续不再 dispatch。
///   3. 连接首次抛异常后触发重连（ConnectAsync 被调用 2 次）。
///   3b. 因错误断开刷新 token；良性关闭不刷新。
///   4. 无活跃会话时循环立即退出，不 connect。
///   5. Dispatcher 的 LoggedOut 事件透传为 NotificationsService 事件。
///   6/7. StartAsync/StopAsync 幂等。
///   8. 账户切换取消旧循环。
/// </summary>
public class NotificationsServiceTests
{
    // ─────────────────────────── 工厂方法 ───────────────────────────

    /// <summary>创建一个已解锁、带真实租约的 VaultSession。</summary>
    private static VaultSession MakeSession(
        string serverUrl = "http://test-server:8080",
        string accessToken = "test-access-token")
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a",
            "test@example.com",
            ServerEndpoints.FromWebVault(serverUrl));
        session.TryCommit(lease, () =>
        {
            session.SetTokens(accessToken, "refresh-token");
            session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        });
        return session;
    }

    /// <summary>创建 NotificationsService，使用近零 backoff 避免测试超时。</summary>
    private static NotificationsService MakeService(
        Func<INotificationsConnection> factory,
        INotificationDispatcher dispatcher,
        VaultSession session,
        ITokenRefresher? refresher = null,
        ISyncService? sync = null,
        TimeSpan? baseBackoff = null) =>
        new(
            factory,
            dispatcher,
            session,
            refresher ?? new FakeTokenRefresher(),
            sync ?? new FakeNotificationSyncService(),
            baseBackoff ?? TimeSpan.FromMilliseconds(10));

    /// <summary>有界等待谓词成立，超时抛 <see cref="OperationCanceledException"/>。</summary>
    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate())
            await Task.Delay(10, cts.Token);
    }

    // ─────────────────────────── 场景 1 ───────────────────────────

    [Fact]
    public async Task StartAsync_ScriptedMessages_AllDispatched_EventsFired()
    {
        var messages = new List<NotificationMessage>
        {
            new((int)UpdateType.SyncVault,      null),
            new((int)UpdateType.SyncSendCreate, null),
            new((int)UpdateType.AuthRequest,    null),
        };

        var conn = new FakeNotificationsConnection { Messages = messages };
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();

        var vaultChangedCount = 0;
        var sendsChangedCount = 0;
        var authChangedCount  = 0;

        var svc = MakeService(() => conn, dispatcher, session);
        svc.VaultChanged        += () => vaultChangedCount++;
        svc.SendsChanged        += () => sendsChangedCount++;
        svc.AuthRequestsChanged += () => authChangedCount++;

        await svc.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dispatcher.Dispatched.Count < 3 && !cts.Token.IsCancellationRequested)
            await Task.Delay(10);

        await svc.StopAsync();

        Assert.Equal(3, dispatcher.Dispatched.Count);
        Assert.True(vaultChangedCount >= 2);
        Assert.True(sendsChangedCount >= 2);
        Assert.Equal(1, authChangedCount);
    }

    // ─────────────────────────── 场景 2 ───────────────────────────

    [Fact]
    public async Task StopAsync_StopsLoop_NoFurtherDispatch()
    {
        var conn = new FakeNotificationsConnection { Messages = new() }; // 空列表
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();

        var svc = MakeService(() => conn, dispatcher, session,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        await svc.StartAsync();

        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (conn.ConnectCallCount == 0 && !waitCts.Token.IsCancellationRequested)
            await Task.Delay(5);

        Assert.True(conn.ConnectCallCount > 0, "loop never connected before StopAsync");
        var countBeforeStop = dispatcher.Dispatched.Count;

        await svc.StopAsync();
        await Task.Delay(100);
        var countAfterStop = dispatcher.Dispatched.Count;

        Assert.Equal(countBeforeStop, countAfterStop);
    }

    // ─────────────────────────── 场景 3 ───────────────────────────

    [Fact]
    public async Task FaultedConnection_TriggersReconnect_ConnectCalledTwice()
    {
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

        await svc.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dispatcher.Dispatched.Count < 1 && !cts.Token.IsCancellationRequested)
            await Task.Delay(10, cts.Token).ConfigureAwait(false);

        await svc.StopAsync();

        Assert.True(conn.ConnectCallCount >= 2,
            $"Expected ConnectCallCount >= 2, got {conn.ConnectCallCount}");
        Assert.Equal(1, dispatcher.Dispatched.Count);
    }

    // ─────────────────────────── 场景 3b（R15/R23）───────────────────────────

    /// <summary>连接因错误断开后应刷新 token（可能已失效）——刷新是重连关键路径。</summary>
    [Fact]
    public async Task FaultedConnection_RefreshesToken()
    {
        var conn = new FakeNotificationsConnection
        {
            ThrowOnConnectCount = 1,
            Messages = new List<NotificationMessage> { new((int)UpdateType.SyncVault, null) },
        };
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();
        var refresher = new FakeTokenRefresher();
        var svc = MakeService(() => conn, dispatcher, session,
            refresher: refresher, baseBackoff: TimeSpan.FromMilliseconds(5));

        await svc.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (dispatcher.Dispatched.Count < 1 && !cts.Token.IsCancellationRequested)
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        await svc.StopAsync();

        Assert.True(refresher.CallCount >= 1,
            $"连接失败后应刷新 token, got {refresher.CallCount}");
    }

    /// <summary>连接正常关闭（token 仍有效）不应刷新——避免每次良性重连多一次 connect/token + 落盘（R15）。</summary>
    [Fact]
    public async Task BenignClose_DoesNotRefreshToken()
    {
        var refresher = new FakeTokenRefresher();
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();
        var svc = MakeService(
            () => new FakeNotificationsConnection { Messages = new List<NotificationMessage>() },
            dispatcher, session, refresher: refresher, baseBackoff: TimeSpan.FromMilliseconds(5));

        await svc.StartAsync();
        await Task.Delay(80).ConfigureAwait(false);
        await svc.StopAsync();

        Assert.Equal(0, refresher.CallCount);
    }

    // ─────────────────────────── 场景 4 ───────────────────────────

    /// <summary>无活跃会话（无租约可捕获）→ 循环立即退出，ConnectAsync 从未被调用。</summary>
    [Fact]
    public async Task NoActiveSession_LoopExitsWithoutConnect()
    {
        var conn = new FakeNotificationsConnection
        {
            Messages = new List<NotificationMessage>
            {
                new((int)UpdateType.SyncVault, null)
            }
        };
        var dispatcher = new FakeNotificationDispatcher();
        var session = new VaultSession(); // 无活跃账户 → CaptureLease 抛出 → StartAsync no-op

        var svc = MakeService(() => conn, dispatcher, session);

        await svc.StartAsync();
        await Task.Delay(100);
        await svc.StopAsync();

        Assert.Equal(0, conn.ConnectCallCount);
        Assert.Empty(dispatcher.Dispatched);
    }

    // ─────────────────────────── 场景 5 ───────────────────────────

    [Fact]
    public async Task DispatcherLoggedOut_ForwardsToServiceEvent()
    {
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

        await svc.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (loggedOutFired < 1 && !cts.Token.IsCancellationRequested)
            await Task.Delay(10);

        await svc.StopAsync();

        Assert.Equal(1, loggedOutFired);
    }

    // ─────────────────────────── 场景 6 ───────────────────────────

    [Fact]
    public async Task StartAsync_CalledTwice_IsIdempotent()
    {
        var connectCount = 0;
        var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        INotificationsConnection ConnFactory()
        {
            return new BlockingFakeConnection(
                onConnect: () => Interlocked.Increment(ref connectCount),
                blockUntil: connectTcs.Task);
        }

        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();
        var svc = MakeService(ConnFactory, dispatcher, session,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        await svc.StartAsync();

        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref connectCount) == 0 && !waitCts.Token.IsCancellationRequested)
            await Task.Delay(5);

        await svc.StartAsync();
        await Task.Delay(50);

        Assert.Equal(1, Volatile.Read(ref connectCount));

        connectTcs.TrySetResult(true);
        await svc.StopAsync();
    }

    // ─────────────────────────── 场景 7 ───────────────────────────

    [Fact]
    public async Task StopAsync_CalledTwice_IsIdempotent()
    {
        var conn = new FakeNotificationsConnection { Messages = new() };
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();
        var svc = MakeService(() => conn, dispatcher, session);

        await svc.StartAsync();
        await svc.StopAsync();
        var ex = await Record.ExceptionAsync(() => svc.StopAsync());
        Assert.Null(ex);
    }

    // ─────────────────────────── 场景 8 ───────────────────────────

    /// <summary>账户切换取消启动租约 → 旧循环退出；不重连、不分发新账户消息。</summary>
    [Fact]
    public async Task AccountSwitch_CancelsOldNotificationLoop()
    {
        var connection = new FakeNotificationsConnection();
        var dispatcher = new FakeNotificationDispatcher();
        var session = MakeSession();
        var service = MakeService(() => connection, dispatcher, session);
        await service.StartAsync();
        await WaitUntilAsync(
            () => connection.ConnectCallCount == 1,
            TimeSpan.FromSeconds(5));

        session.BeginAccountSession(
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"));
        await WaitUntilAsync(
            () => connection.ReadCancellationObserved,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.Equal(1, connection.ConnectCallCount);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Theory]
    [InlineData("https://vault.example/warden", "https://vault.example/warden/notifications")]
    [InlineData("https://vault.bitwarden.com", "https://notifications.bitwarden.com")]
    public async Task StartAsync_UsesNotificationsEndpointFromLease(
        string webVault,
        string expectedNotificationsEndpoint)
    {
        var connection = new CapturingNotificationsConnection();
        var service = MakeService(
            () => connection,
            new FakeNotificationDispatcher(),
            MakeSession(webVault));

        await service.StartAsync();
        await WaitUntilAsync(
            () => connection.ServerUrl is not null,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.Equal(expectedNotificationsEndpoint, connection.ServerUrl);
    }

    [Fact]
    public async Task InitialConnection_RunsFullCatchUpAndRaisesRefreshEvents()
    {
        var connection = new FakeNotificationsConnection();
        var dispatcher = new FakeNotificationDispatcher();
        var sync = new FakeNotificationSyncService();
        var session = MakeSession();
        var vaultChanged = 0;
        var sendsChanged = 0;
        var service = MakeService(
            () => connection,
            dispatcher,
            session,
            sync: sync);
        service.VaultChanged += () => vaultChanged++;
        service.SendsChanged += () => sendsChanged++;

        await service.StartAsync();
        await WaitUntilAsync(
            () => sync.SyncCalls == 1
                && Volatile.Read(ref vaultChanged) == 1
                && Volatile.Read(ref sendsChanged) == 1,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.True(sync.Leases.Single().SameSessionAs(session.CaptureLease()));
        Assert.Equal(1, vaultChanged);
        Assert.Equal(1, sendsChanged);
    }

    [Fact]
    public async Task CatchUpFailure_ReconnectsAndRetriesBeforeLiveMessageDispatch()
    {
        var connection = new FakeNotificationsConnection
        {
            Messages = [new NotificationMessage((int)UpdateType.SyncVault, null)],
        };
        var dispatcher = new FakeNotificationDispatcher();
        var refresher = new FakeTokenRefresher();
        var sync = new FakeNotificationSyncService
        {
            NextSyncException = new HttpRequestException("sync unavailable"),
        };
        var service = MakeService(
            () => connection,
            dispatcher,
            MakeSession(),
            refresher: refresher,
            sync: sync,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        await service.StartAsync();
        await WaitUntilAsync(
            () => dispatcher.Dispatched.Count >= 1,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.True(sync.SyncCalls >= 2);
        Assert.True(connection.ConnectCallCount >= 2);
        Assert.Equal(UpdateType.SyncVault, (UpdateType)dispatcher.Dispatched[0].Type);
        Assert.True(refresher.CallCount >= 1);
    }

    [Fact]
    public async Task PersistentCatchUpFailure_NeverAdmitsLiveMessagesAndStopsOnCancellation()
    {
        var connection = new FakeNotificationsConnection
        {
            Messages = [new NotificationMessage((int)UpdateType.SyncVault, null)],
        };
        var dispatcher = new FakeNotificationDispatcher();
        var sync = new FakeNotificationSyncService
        {
            SyncException = new HttpRequestException("sync unavailable"),
        };
        var service = MakeService(
            () => connection,
            dispatcher,
            MakeSession(),
            sync: sync,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        await service.StartAsync();
        await WaitUntilAsync(
            () => sync.SyncCalls >= 2,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.True(connection.ConnectCallCount >= 2);
        Assert.Empty(dispatcher.Dispatched);
    }

    [Fact]
    public async Task SuccessfulReconnect_RunsCatchUpAgain()
    {
        var connection = new FakeNotificationsConnection();
        connection.MessageQueues.Enqueue([]);
        var sync = new FakeNotificationSyncService();
        var service = MakeService(
            () => connection,
            new FakeNotificationDispatcher(),
            MakeSession(),
            sync: sync,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        await service.StartAsync();
        await WaitUntilAsync(
            () => sync.SyncCalls >= 2,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.True(connection.ConnectCallCount >= 2);
        Assert.True(sync.Leases.All(
            lease => lease.SameSessionAs(sync.Leases[0])));
    }

    [Fact]
    public async Task SyncOrgKeys_RefreshesTokenBeforeRebuildingConnection()
    {
        var connection = new FakeNotificationsConnection
        {
            Messages = [new NotificationMessage((int)UpdateType.SyncOrgKeys, null)],
        };
        var session = MakeSession();
        var refresher = new FakeTokenRefresher
        {
            Handler = (lease, _) => Task.FromResult(session.TryCommit(lease, () =>
                session.SetTokens("rotated-access", "rotated-refresh"))),
        };
        var service = MakeService(
            () => connection,
            new FakeNotificationDispatcher(),
            session,
            refresher: refresher,
            baseBackoff: TimeSpan.FromSeconds(5));

        await service.StartAsync();
        await WaitUntilAsync(
            () => connection.ConnectCallCount >= 2,
            TimeSpan.FromSeconds(2));
        await service.StopAsync();

        Assert.True(connection.ConnectCallCount >= 2);
        Assert.True(refresher.CallCount >= 1);
        Assert.Equal("test-access-token", connection.AccessTokens[0]);
        Assert.Equal("rotated-access", connection.AccessTokens[1]);
    }

    [Fact]
    public async Task SyncOrgKeys_RefreshFailureDoesNotReconnectWithOldToken()
    {
        var connection = new FakeNotificationsConnection
        {
            Messages = [new NotificationMessage((int)UpdateType.SyncOrgKeys, null)],
        };
        var refresher = new FakeTokenRefresher { Result = false };
        var service = MakeService(
            () => connection,
            new FakeNotificationDispatcher(),
            MakeSession(),
            refresher: refresher,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        await service.StartAsync();
        await WaitUntilAsync(
            () => refresher.CallCount >= 2,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.Equal(1, connection.ConnectCallCount);
        Assert.Single(connection.AccessTokens);
        Assert.Equal("test-access-token", connection.AccessTokens[0]);
    }

    [Fact]
    public async Task SyncOrgKeys_PostSyncCallbackFailureStillUsesRefreshBarrier()
    {
        var connection = new FakeNotificationsConnection
        {
            Messages = [new NotificationMessage((int)UpdateType.SyncOrgKeys, null)],
        };
        var session = MakeSession();
        var refresher = new FakeTokenRefresher
        {
            Handler = (lease, _) => Task.FromResult(session.TryCommit(lease, () =>
                session.SetTokens("rotated-after-failure", "rotated-refresh"))),
        };
        var service = MakeService(
            () => connection,
            new FakeNotificationDispatcher { ThrowAfterConnectionRebuild = true },
            session,
            refresher: refresher,
            baseBackoff: TimeSpan.FromMilliseconds(5));

        await service.StartAsync();
        await WaitUntilAsync(
            () => connection.ConnectCallCount >= 2,
            TimeSpan.FromSeconds(5));
        await service.StopAsync();

        Assert.True(refresher.CallCount >= 1);
        Assert.Equal("rotated-after-failure", connection.AccessTokens[1]);
    }

    private sealed class CapturingNotificationsConnection : INotificationsConnection
    {
        public string? ServerUrl { get; private set; }

        public Task ConnectAsync(string serverUrl, string accessToken, CancellationToken ct)
        {
            ServerUrl = serverUrl;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<NotificationMessage> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
