using System.Net;
using System.Text.Json;
using Api;
using Core.Models;
using Core.Session;
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
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

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
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

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
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

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
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

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
    public async Task ReadError_404WithJsonBody_ThrowsVaultApiExceptionWithStatusAndServerMessage()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.NotFound,
            """{"message":"Folder doesn't exist","validationErrors":{},"object":"error"}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var ex = await Assert.ThrowsAsync<VaultApiException>(() =>
            client.GetFolderAsync("missing", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("Folder doesn't exist", ex.ServerMessage);
        Assert.Contains("Folder doesn't exist", ex.Message);
        // 派生自 HttpRequestException:既有异常处理器行为不变。
        Assert.IsAssignableFrom<HttpRequestException>(ex);
    }

    [Fact]
    public async Task ReadError_NonJsonBody_ThrowsVaultApiExceptionWithNullServerMessage()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream exploded", System.Text.Encoding.UTF8, "text/plain"),
        });
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var ex = await Assert.ThrowsAsync<VaultApiException>(() =>
            client.GetSyncAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Null(ex.ServerMessage);
        Assert.IsAssignableFrom<HttpRequestException>(ex);
    }

    [Fact]
    public async Task GetConfig_ReadsServerObjectAndVersion()
    {
        // 真实 /api/config 把 server 作为对象返回(name/url),曾被误建模为 string。
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"version":"2025.12.0","gitHash":"f21a3ada","object":"config","server":{"name":"Vaultwarden","url":"https://github.com/dani-garcia/vaultwarden"},"settings":{"disableUserRegistration":false},"environment":{"vault":"http://localhost"}}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

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
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

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
            new ServerOrigin("https://vault.example"),
            () => new AccessTokenBinding("acct", new ServerOrigin("https://vault.example"), 1, "access-token"),
            _ => Task.FromResult(false))
        {
            InnerHandler = handler,
        })
        {
            BaseAddress = new Uri("https://vault.example/"),
        };

        await client.GetAsync("api/config", TestContext.Current.CancellationToken);

        var request = handler.Requests[0];
        Assert.Equal(ClientHeadersHandler.ClientName, request.Headers.GetValues("Bitwarden-Client-Name").Single());
        Assert.Equal(ClientHeadersHandler.ClientVersion, request.Headers.GetValues("Bitwarden-Client-Version").Single());
        Assert.Equal(ClientHeadersHandler.DeviceType, request.Headers.GetValues("Device-Type").Single());
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
            new ServerOrigin("https://vault.example"),
            () => new AccessTokenBinding("acct", new ServerOrigin("https://vault.example"), 1, accessToken),
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
            new ServerOrigin("https://vault.example"),
            () => new AccessTokenBinding("acct", new ServerOrigin("https://vault.example"), 1, "token"),
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

    // R2 回归:PUT /api/two-factor/email 成功响应把 enabled 序列化为字符串 "true"
    // (vaultwarden email.rs:241)。EnableEmailAsync 不得因解析该 body 抛异常——
    // 否则启用邮箱2FA 永远显示失败(服务端其实已启用)。
    [Fact]
    public async Task EnableEmail_ServerReturnsStringEnabled_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"email":"me@example.com","enabled":"true","object":"twoFactorEmail"}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

        // 不抛即通过(旧实现会因 "true"→bool 反序列化抛 JsonException)。
        await client.EnableEmailAsync(
            new EmailVerifyRequest("me@example.com", "TOKEN", "hash"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/two-factor/email", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"token\":\"TOKEN\"", handler.Bodies[0]);
    }

    // A2:vaultwarden 默认错误体把 error/error_description 置空串,真实文案只在 message
    // (error.rs:227/239-240)。旧实现只读 error_description → 显示空白。回退到 message。
    [Fact]
    public async Task ConnectToken_PasswordError_FallsBackToMessageField()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"message":"Username or password is incorrect. Try again","validationErrors":{"":["Username or password is incorrect. Try again"]},"errorModel":{"message":"Username or password is incorrect. Try again","object":"error"},"error":"","error_description":"","object":"error"}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.Password("me@example.com", "hash", "d", "n", null),
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<ConnectTokenResult.Error>(result);
        Assert.Equal("Username or password is incorrect. Try again", error.Message);
        Assert.Equal((int)HttpStatusCode.BadRequest, error.HttpStatus);
        Assert.Null(error.ErrorCode); // error 字段为空串 → 无可辨识错误码
    }

    // A2/B1 支撑:刷新失效体 {"error":"invalid_grant"} 需把 ErrorCode 暴露给 TokenRefreshService。
    [Fact]
    public async Task ConnectToken_RefreshInvalidGrant_ExposesErrorCode()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant"}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.Refresh("stale-refresh", "d", "n"),
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<ConnectTokenResult.Error>(result);
        Assert.Equal("invalid_grant", error.ErrorCode);
        Assert.Equal((int)HttpStatusCode.BadRequest, error.HttpStatus);
    }

    // A2:限流(429)可通过 HttpStatus 辨识,与密码错(400)区分。
    [Fact]
    public async Task ConnectToken_RateLimited_ExposesStatus429()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, "{}"));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.Password("me@example.com", "hash", "d", "n", null),
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<ConnectTokenResult.Error>(result);
        Assert.Equal(429, error.HttpStatus);
        Assert.Null(error.ErrorCode);
    }

    [Fact]
    public async Task SendTwoFactorEmailLogin_PostsUnauthenticatedLoginEndpointWithBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

        await client.SendTwoFactorEmailLoginAsync(
            "me@example.com", "hash-abc", "device-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/two-factor/send-email-login", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"me@example.com\"", handler.Bodies[0]);
        Assert.Contains("\"masterPasswordHash\":\"hash-abc\"", handler.Bodies[0]);
        Assert.Contains("\"deviceIdentifier\":\"device-1\"", handler.Bodies[0]);
    }
}
