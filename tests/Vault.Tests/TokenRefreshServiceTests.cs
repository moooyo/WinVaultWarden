using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Core.Session;
using Vault;
using Xunit;

namespace Vault.Tests;

public class TokenRefreshServiceTests
{
    private const string ServerUrl = "https://vault.example";
    private const string Email = "me@example.com";

    private static TokenRefreshService Create(FakeHttpMessageHandler handler, VaultSession session, MemoryTokenStore tokenStore)
    {
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(ServerUrl));
        return new TokenRefreshService(FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(api), session, tokenStore);
    }

    private static VaultSession ActiveSession()
    {
        var session = new VaultSession();
        session.BeginAccountSession(Email, Email, new ServerOrigin(ServerUrl));
        return session;
    }

    private static MemoryTokenStore StoreWithSession()
    {
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin(ServerUrl), 1, Email, "device-id", "old-refresh", "2.protected",
            KdfType.Pbkdf2, 600_000, null, null));
        return store;
    }

    private const string RefreshOkJson =
        """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}""";

    [Fact]
    public async Task TryRefreshAsync_Success_UpdatesSessionAndPersistsRotatedToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, RefreshOkJson));
        var session = ActiveSession();
        session.SetTokens("old-access", "old-refresh");
        var store = StoreWithSession();
        var service = Create(handler, session, store);

        var ok = await service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal("new-access", session.AccessToken);
        Assert.Equal("new-refresh", session.RefreshToken);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal("new-refresh", persisted.RefreshToken);
        Assert.Contains("grant_type=refresh_token", handler.Bodies[0]);
    }

    [Fact]
    public async Task TryRefreshAsync_NoRefreshToken_ReturnsFalseWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler();
        var session = ActiveSession(); // RefreshToken == null (BeginAccountSession clears tokens)
        var service = Create(handler, session, StoreWithSession());

        var ok = await service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryRefreshAsync_NoPersistedSession_ReturnsFalseWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler();
        var session = ActiveSession();
        session.SetTokens("old-access", "old-refresh");
        var service = Create(handler, session, new MemoryTokenStore());

        var ok = await service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryRefreshAsync_ServerRejects_ReturnsFalseAndLocks()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"refresh token is invalid"}"""));
        var session = ActiveSession();
        session.SetTokens("old-access", "old-refresh");
        var service = Create(handler, session, StoreWithSession());

        var ok = await service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Equal(VaultState.Locked, session.State);
    }

    [Fact]
    public async Task TryRefreshAsync_NetworkException_ReturnsFalseWithoutLocking()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("connection refused"));
        var session = ActiveSession();
        session.SetTokens("old-access", "old-refresh");
        session.SetState(VaultState.Unlocked);
        var service = Create(handler, session, StoreWithSession());

        var ok = await service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.NotEqual(VaultState.Locked, session.State);
        Assert.Equal("old-access", session.AccessToken);
    }

    [Fact]
    public async Task TryRefreshAsync_StaleInvalidGrant_DoesNotLockNewSession()
    {
        // Session A 的刷新请求在途期间，用户切换至 Session B。
        // A 收到的 invalid_grant 响应绝不能锁定 B 的会话。
        var httpStarted = new TaskCompletionSource();
        var switchComplete = new TaskCompletionSource();

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            httpStarted.TrySetResult();   // 通知主线程：HTTP 已发出
            switchComplete.Task.Wait();   // 等待 B 登录完成后再返回响应
            return FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
                """{"error":"invalid_grant","error_description":"refresh token is invalid"}""");
        });

        var session = ActiveSession();   // Session A (me@example.com)
        session.SetTokens("old-access", "old-refresh");
        var service = Create(handler, session, StoreWithSession());

        // 用 Task.Run 防止 handler 内的同步 Wait 死锁主线程。
        var refreshA = Task.Run(() => service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken));

        await httpStarted.Task;   // A 的 HTTP 已在途

        // 切换到 B：轮换 generation，A 的 lease 作废。
        session.BeginAccountSession("b@example.com", "b@example.com", new ServerOrigin(ServerUrl));
        session.SetTokens("b-access", "b-refresh");
        session.SetState(VaultState.Unlocked);
        switchComplete.SetResult();   // 允许 A 的响应返回

        var ok = await refreshA;

        Assert.False(ok);
        Assert.NotEqual(VaultState.Locked, session.State);   // B 的会话未被锁定
        Assert.Equal("b-access", session.AccessToken);        // B 的 token 仍然有效
    }

    [Fact]
    public async Task TryRefreshAsync_ConcurrentCallers_RefreshesOnce()
    {
        var gate = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            gate.TrySetResult();
            release.Task.Wait();
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, RefreshOkJson);
        });
        var session = ActiveSession();
        session.SetTokens("old-access", "old-refresh");
        var service = Create(handler, session, StoreWithSession());

        var first = Task.Run(() => service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken));
        await gate.Task; // 第一个调用已进入 HTTP(持有信号量)
        var second = service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken); // 第二个排队等信号量
        release.SetResult();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Single(handler.Requests); // 单飞:只发一次刷新
        Assert.Equal("new-access", session.AccessToken);
    }

    [Fact]
    public async Task TryRefreshAsync_ServerError5xx_ReturnsFalseWithoutLocking()
    {
        var handler = new FakeHttpMessageHandler();
        // 反代 502 常见:非 JSON 的 HTML 体。ReadJsonOrNull 解析失败 → ErrorCode 为 null → 视为瞬时。
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>"));
        var session = ActiveSession();
        session.SetTokens("old-access", "old-refresh");
        session.SetState(VaultState.Unlocked);
        var service = Create(handler, session, StoreWithSession());

        var ok = await service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.NotEqual(VaultState.Locked, session.State);
        Assert.Equal("old-access", session.AccessToken); // Lock() 会清空 AccessToken → 证明未锁
    }

    [Fact]
    public async Task TryRefreshAsync_RateLimited429_ReturnsFalseWithoutLocking()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, "{}"));
        var session = ActiveSession();
        session.SetTokens("old-access", "old-refresh");
        session.SetState(VaultState.Unlocked);
        var service = Create(handler, session, StoreWithSession());

        var ok = await service.TryRefreshAsync(session.CaptureLease(), TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.NotEqual(VaultState.Locked, session.State);
        Assert.Equal("old-access", session.AccessToken);
    }
}
