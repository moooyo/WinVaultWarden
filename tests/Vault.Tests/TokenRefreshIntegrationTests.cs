using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Core.Session;
using Vault;
using Xunit;

namespace Vault.Tests;

public class TokenRefreshIntegrationTests
{
    [Fact]
    public async Task Pipeline_On401_RefreshesAndRetriesWithNewToken()
    {
        var origin = new ServerOrigin("https://vault.example");
        var session = new VaultSession();
        session.BeginAccountSession("me@example.com", "me@example.com", origin);
        session.SetTokens("old-access", "old-refresh");
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, "me@example.com", new ServerOrigin("https://vault.example"), 1, "me@example.com", "device-id",
            "old-refresh", "2.protected", KdfType.Pbkdf2, 600_000, null, null));

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));            // 1) GET api/sync → 401
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,                    // 2) POST connect/token → 200
            """{"access_token":"e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));             // 3) GET api/sync 重试 → 200

        // 延迟解析打破构造环:回调捕获稍后赋值的 refresher。
        TokenRefreshService refresher = null!;
        var authHandler = new AuthHeaderHandler(
            origin,
            () => session.AccessToken is { } token
                ? new AccessTokenBinding("me@example.com", origin, session.Generation, token)
                : null,
            ct => refresher.TryRefreshAsync(session.CaptureLease(), ct))
        {
            InnerHandler = handler,
        };
        using var pipeline = new HttpClient(authHandler) { BaseAddress = new Uri("https://vault.example/") };
        var api = new ApiClient(pipeline, origin);
        refresher = new TokenRefreshService(
            FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(api), session, tokenStore);

        var response = await pipeline.GetAsync("api/sync", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln",
            session.AccessToken);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("/api/sync", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/identity/connect/token", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("/api/sync", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Equal(
            "e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln",
            handler.Requests[2].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Pipeline_On401_DoesNotRetryWithAnotherAccountsToken()
    {
        var origin = new ServerOrigin("https://vault.example");
        var session = new VaultSession();
        session.BeginAccountSession("me@example.com", "me@example.com", origin);
        session.SetTokens("old-access", "old-refresh");
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "me@example.com",
            origin,
            1,
            "me@example.com",
            "device-id",
            "old-refresh",
            "2.protected",
            KdfType.Pbkdf2,
            600_000,
            null,
            null));

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"access_token":"e30.eyJzdWIiOiJvdGhlckBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));

        TokenRefreshService refresher = null!;
        var authHandler = new AuthHeaderHandler(
            origin,
            () => session.AccessToken is { } token
                ? new AccessTokenBinding(
                    "me@example.com",
                    origin,
                    session.Generation,
                    token)
                : null,
            ct => refresher.TryRefreshAsync(session.CaptureLease(), ct))
        {
            InnerHandler = handler,
        };
        using var pipeline = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("https://vault.example/"),
        };
        var api = new ApiClient(pipeline, origin);
        refresher = new TokenRefreshService(
            FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(api),
            session,
            tokenStore);

        var response = await pipeline.GetAsync(
            "api/sync",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("old-access", session.AccessToken);
        Assert.Equal("old-refresh", session.RefreshToken);
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal("old-refresh", persisted.RefreshToken);
    }
}
