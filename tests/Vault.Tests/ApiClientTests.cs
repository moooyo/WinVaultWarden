using System.Net;
using System.Text.Json;
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
    public void SyncResponse_ReadsLoginFido2Credentials()
    {
        var sync = JsonSerializer.Deserialize<SyncResponse>(
            """
            {
              "object": "sync",
              "profile": { "email": "me@example.com" },
              "folders": [],
              "ciphers": [
                {
                  "id": "c-passkey",
                  "type": 1,
                  "name": "2.name",
                  "notes": null,
                  "key": null,
                  "organizationId": null,
                  "folderId": null,
                  "favorite": false,
                  "reprompt": 0,
                  "login": {
                    "username": null,
                    "password": null,
                    "totp": null,
                    "uris": [],
                    "fido2Credentials": [
                      {
                        "credentialId": "2.credential",
                        "keyType": "2.public-key",
                        "keyAlgorithm": "2.ECDSA",
                        "keyCurve": "2.P-256",
                        "keyValue": "2.private-key",
                        "rpId": "2.example.com",
                        "userHandle": "2.user-handle",
                        "userName": "2.user@example.com",
                        "counter": "2.7",
                        "rpName": "2.Example",
                        "userDisplayName": "2.User",
                        "discoverable": "2.true",
                        "creationDate": "2026-06-24T00:00:00Z"
                      }
                    ]
                  },
                  "card": null,
                  "identity": null,
                  "secureNote": null,
                  "sshKey": null,
                  "fields": null,
                  "creationDate": "2026-06-24T00:00:00Z",
                  "revisionDate": "2026-06-24T00:00:00Z",
                  "deletedDate": null
                }
              ]
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var credential = Assert.Single(sync.Ciphers![0].Login!.Fido2Credentials!);

        Assert.Equal("2.credential", credential.CredentialId);
        Assert.Equal("2.public-key", credential.KeyType);
        Assert.Equal("2.ECDSA", credential.KeyAlgorithm);
        Assert.Equal("2.P-256", credential.KeyCurve);
        Assert.Equal("2.private-key", credential.KeyValue);
        Assert.Equal("2.example.com", credential.RpId);
        Assert.Equal("2.user-handle", credential.UserHandle);
        Assert.Equal("2.user@example.com", credential.UserName);
        Assert.Equal("2.7", credential.Counter);
        Assert.Equal("2.Example", credential.RpName);
        Assert.Equal("2.User", credential.UserDisplayName);
        Assert.Equal("2.true", credential.Discoverable);
        Assert.Equal(DateTimeOffset.Parse("2026-06-24T00:00:00Z"), credential.CreationDate);
    }

    [Fact]
    public async Task GetConfig_ReadsServerObjectAndVersion()
    {
        // 真实 /api/config 把 server 作为对象返回(name/url),曾被误建模为 string。
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"version":"2025.12.0","gitHash":"f21a3ada","object":"config","server":{"name":"Vaultwarden","url":"https://github.com/dani-garcia/vaultwarden"},"settings":{"disableUserRegistration":false},"environment":{"vault":"http://localhost"}}"""));
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");

        var config = await client.GetConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/api/config", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("2025.12.0", config.Version);
        Assert.Equal("Vaultwarden", config.Server!.Name);
        Assert.Equal("https://github.com/dani-garcia/vaultwarden", config.Server.Url);
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

    [Fact]
    public async Task AuthHeaderHandler_DoesNotRefreshOnConnectTokenEndpoint()
    {
        var refreshCalls = 0;
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new HttpClient(new AuthHeaderHandler(
            () => "token",
            _ => { refreshCalls++; return Task.FromResult(true); })
        {
            InnerHandler = handler,
        })
        {
            BaseAddress = new Uri("https://vault.example/"),
        };

        var response = await client.PostAsync(
            "identity/connect/token",
            new StringContent("grant_type=refresh_token"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, refreshCalls);
        Assert.Single(handler.Requests);
    }
}
