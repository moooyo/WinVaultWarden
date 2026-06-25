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
        var api = new ApiClient(new HttpClient(handler));
        return new TokenRefreshService(api, session, tokenStore);
    }

    private static MemoryTokenStore StoreWithSession()
    {
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(ServerUrl, Email, "device-id", "old-refresh", "2.protected",
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
        var session = new VaultSession();
        session.SetTokens("old-access", "old-refresh");
        var store = StoreWithSession();
        var service = Create(handler, session, store);

        var ok = await service.TryRefreshAsync(TestContext.Current.CancellationToken);

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
        var session = new VaultSession(); // RefreshToken == null
        var service = Create(handler, session, StoreWithSession());

        var ok = await service.TryRefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryRefreshAsync_NoPersistedSession_ReturnsFalseWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        session.SetTokens("old-access", "old-refresh");
        var service = Create(handler, session, new MemoryTokenStore());

        var ok = await service.TryRefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TryRefreshAsync_ServerRejects_ReturnsFalseAndLocks()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"refresh token is invalid"}"""));
        var session = new VaultSession();
        session.SetTokens("old-access", "old-refresh");
        var service = Create(handler, session, StoreWithSession());

        var ok = await service.TryRefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(ok);
        Assert.Equal(VaultState.Locked, session.State);
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
        var session = new VaultSession();
        session.SetTokens("old-access", "old-refresh");
        var service = Create(handler, session, StoreWithSession());

        var first = Task.Run(() => service.TryRefreshAsync(TestContext.Current.CancellationToken));
        await gate.Task; // 第一个调用已进入 HTTP(持有信号量)
        var second = service.TryRefreshAsync(TestContext.Current.CancellationToken); // 第二个排队等信号量
        release.SetResult();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Single(handler.Requests); // 单飞:只发一次刷新
        Assert.Equal("new-access", session.AccessToken);
    }
}
