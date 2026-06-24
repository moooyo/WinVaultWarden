using System.Net;
using Api;
using Api.Dtos;
using Core.Enums;
using Xunit;

namespace Vault.Tests;

public class ApiClientTests
{
    [Fact]
    public async Task Prelogin_PostsExpectedPathAndEmail()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"kdf":0,"kdfIterations":600000,"kdfMemory":null,"kdfParallelism":null}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example/");

        var response = await client.PreloginAsync("me@example.com", TestContext.Current.CancellationToken);

        Assert.Equal(KdfType.Pbkdf2, response.Kdf);
        Assert.Equal(600000, response.KdfIterations);
        Assert.Equal("/identity/accounts/prelogin", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"me@example.com\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task ConnectToken_PasswordGrant_UsesFormFields()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"2.X","PrivateKey":null,"Kdf":0,"KdfIterations":600000}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var result = await client.ConnectTokenAsync(ConnectTokenRequest.Password(
            "me@example.com",
            "hash",
            "device-id",
            "WinVaultWarden",
            null), TestContext.Current.CancellationToken);

        var success = Assert.IsType<ConnectTokenResult.Success>(result);
        Assert.Equal("a", success.Token.AccessToken);
        Assert.Equal("/identity/connect/token", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("grant_type=password", handler.Bodies[0]);
        Assert.Contains("client_id=desktop", handler.Bodies[0]);
        Assert.Contains("device_type=6", handler.Bodies[0]);
        Assert.Contains("username=me%40example.com", handler.Bodies[0]);
    }

    [Fact]
    public async Task ConnectToken_TwoFactorError_ReturnsTwoFactorRequired()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"],"TwoFactorProviders2":{"0":null}}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.Password("me@example.com", "hash", "d", "n", null),
            TestContext.Current.CancellationToken);

        var twoFactor = Assert.IsType<ConnectTokenResult.TwoFactorRequired>(result);
        Assert.Equal([0], twoFactor.Providers);
    }

    [Fact]
    public async Task GetSync_UsesExcludeDomainsQuery()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"object":"sync","profile":{"email":"me@example.com"},"folders":[],"ciphers":[]}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var sync = await client.GetSyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sync", sync.Object);
        Assert.Equal("/api/sync", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("excludeDomains=true", handler.Requests[0].RequestUri!.Query.TrimStart('?'));
    }

    [Fact]
    public async Task GetDevices_ReadsListResponse()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var devices = await client.GetDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/api/devices", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Single(devices.Data);
        Assert.Equal("Desktop", devices.Data[0].Name);
    }

    [Fact]
    public async Task AuthHeaderHandler_AddsBitwardenHeadersAndBearerToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(new AuthHeaderHandler(
            () => "access-token",
            _ => Task.FromResult(false))
        {
            InnerHandler = handler,
        })
        {
            BaseAddress = new Uri("https://vault.example/"),
        };

        await client.GetAsync("api/config", TestContext.Current.CancellationToken);

        var request = handler.Requests[0];
        Assert.Equal(AuthHeaderHandler.ClientName, request.Headers.GetValues("Bitwarden-Client-Name").Single());
        Assert.Equal(AuthHeaderHandler.ClientVersion, request.Headers.GetValues("Bitwarden-Client-Version").Single());
        Assert.Equal(AuthHeaderHandler.DeviceType, request.Headers.GetValues("Device-Type").Single());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("access-token", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task AuthHeaderHandler_RetriesOnceAfterSuccessfulRefresh()
    {
        var accessToken = "old-token";
        var refreshCalls = 0;
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        using var client = new HttpClient(new AuthHeaderHandler(
            () => accessToken,
            _ =>
            {
                refreshCalls++;
                accessToken = "new-token";
                return Task.FromResult(true);
            })
        {
            InnerHandler = handler,
        })
        {
            BaseAddress = new Uri("https://vault.example/"),
        };

        var response = await client.GetAsync("api/sync", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("old-token", handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal("new-token", handler.Requests[1].Headers.Authorization!.Parameter);
    }
}
