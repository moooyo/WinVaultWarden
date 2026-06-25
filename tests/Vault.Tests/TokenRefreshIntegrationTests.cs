using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public class TokenRefreshIntegrationTests
{
    [Fact]
    public async Task Pipeline_On401_RefreshesAndRetriesWithNewToken()
    {
        var session = new VaultSession();
        session.SetTokens("old-access", "old-refresh");
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession("https://vault.example", "me@example.com", "device-id",
            "old-refresh", "2.protected", KdfType.Pbkdf2, 600_000, null, null));

        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));            // 1) GET api/sync → 401
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,                    // 2) POST connect/token → 200
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));             // 3) GET api/sync 重试 → 200

        // 延迟解析打破构造环:回调捕获稍后赋值的 refresher。
        TokenRefreshService refresher = null!;
        var authHandler = new AuthHeaderHandler(
            () => session.AccessToken,
            ct => refresher.TryRefreshAsync(ct))
        {
            InnerHandler = handler,
        };
        using var pipeline = new HttpClient(authHandler) { BaseAddress = new Uri("https://vault.example/") };
        var api = new ApiClient(pipeline);
        api.SetBaseAddress("https://vault.example");
        refresher = new TokenRefreshService(api, session, tokenStore);

        var response = await pipeline.GetAsync("api/sync", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("new-access", session.AccessToken);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("/api/sync", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/identity/connect/token", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("/api/sync", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Equal("new-access", handler.Requests[2].Headers.Authorization!.Parameter);
    }
}
