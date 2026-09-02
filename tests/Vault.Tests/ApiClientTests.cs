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
        Assert.Contains("deviceType=6", handler.Bodies[0]);
        Assert.Contains("deviceIdentifier=device-id", handler.Bodies[0]);
        Assert.Contains("deviceName=WinVaultWarden", handler.Bodies[0]);
        Assert.DoesNotContain("device_type", handler.Bodies[0]);
        Assert.Contains("username=me%40example.com", handler.Bodies[0]);
        Assert.DoesNotContain("newDeviceOtp", handler.Bodies[0]);
    }

    [Fact]
    public async Task ConnectToken_PasswordGrant_WithNewDeviceOtp_UsesExactFormField()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"2.X","PrivateKey":null,"Kdf":0,"KdfIterations":600000}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.Password(
                "me@example.com",
                "hash",
                "device-id",
                "WinVaultWarden",
                null,
                "123456"),
            TestContext.Current.CancellationToken);

        Assert.IsType<ConnectTokenResult.Success>(result);
        var form = ParseForm(handler.Bodies[0]);
        Assert.Equal("123456", form["newDeviceOtp"]);
        Assert.False(form.ContainsKey("new_device_otp"));
        Assert.False(form.ContainsKey("NewDeviceOtp"));
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
    public async Task ConnectToken_NewDeviceVerificationError_ReturnsDedicatedResult()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"New device verification required","ErrorModel":{"Message":"new device verification required","Object":"error"}}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.Password("me@example.com", "hash", "d", "n", null),
            TestContext.Current.CancellationToken);

        Assert.IsType<ConnectTokenResult.DeviceVerificationRequired>(result);
    }

    [Fact]
    public async Task ConnectToken_OtherDeviceError_RemainsError()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"Invalid New Device OTP","ErrorModel":{"Message":"invalid new device otp","Object":"error"}}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.Password("me@example.com", "hash", "d", "n", null),
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<ConnectTokenResult.Error>(result);
        Assert.Equal("Invalid New Device OTP", error.Message);
        Assert.Equal("device_error", error.ErrorCode);
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
    public void ClientProtocolVersion_TracksSupportedDesktopContract()
    {
        Assert.Equal("desktop", ClientHeadersHandler.ClientName);
        Assert.Equal("2026.8.0", ClientHeadersHandler.ClientVersion);
        Assert.True(Version.TryParse(ClientHeadersHandler.ClientVersion, out _));
    }

    [Fact]
    public async Task CipherArchiveEndpoints_UseVaultwardenRoutesAndIdsPayloads()
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < 4; i++)
            handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));

        await client.ArchiveCipherAsync("single-a", TestContext.Current.CancellationToken);
        await client.UnarchiveCipherAsync("single-b", TestContext.Current.CancellationToken);
        await client.BulkArchiveCiphersAsync(["a", "b"], TestContext.Current.CancellationToken);
        await client.BulkUnarchiveCiphersAsync(["c", "d"], TestContext.Current.CancellationToken);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/ciphers/single-a/archive", request.RequestUri!.AbsolutePath);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/ciphers/single-b/unarchive", request.RequestUri!.AbsolutePath);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/ciphers/archive", request.RequestUri!.AbsolutePath);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/ciphers/unarchive", request.RequestUri!.AbsolutePath);
            });
        Assert.Contains("\"ids\":[\"a\",\"b\"]", handler.Bodies[2]);
        Assert.Contains("\"ids\":[\"c\",\"d\"]", handler.Bodies[3]);
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
            new SendEmailLoginRequest("me@example.com", "hash-abc", "device-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/two-factor/send-email-login", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"me@example.com\"", handler.Bodies[0]);
        Assert.Contains("\"masterPasswordHash\":\"hash-abc\"", handler.Bodies[0]);
        Assert.Contains("\"deviceIdentifier\":\"device-1\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task ResendNewDeviceOtp_PostsExactAnonymousRequest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = new ApiClientFactory(_ => handler)
            .CreateAnonymous<IIdentityApiClient>(new ServerOrigin("https://vault.example/"));

        await client.ResendNewDeviceOtpAsync(
            new ResendNewDeviceOtpRequest("me@example.com", "hash-abc"),
            TestContext.Current.CancellationToken);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/accounts/resend-new-device-otp", sent.RequestUri!.AbsolutePath);
        Assert.Null(sent.Headers.Authorization);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var properties = body.RootElement.EnumerateObject().ToArray();
        Assert.Equal(2, properties.Length);
        Assert.Equal("me@example.com", body.RootElement.GetProperty("email").GetString());
        Assert.Equal("hash-abc", body.RootElement.GetProperty("masterPasswordHash").GetString());
    }

    [Theory]
    [InlineData("", "hash")]
    [InlineData("me@example.com", "")]
    [InlineData(" ", "hash")]
    [InlineData("me@example.com", " ")]
    public async Task ResendNewDeviceOtp_RejectsBlankCredentials(
        string email,
        string masterPasswordHash)
    {
        var handler = new FakeHttpMessageHandler();
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ResendNewDeviceOtpAsync(
            new ResendNewDeviceOtpRequest(email, masterPasswordHash),
            TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ResendNewDeviceOtp_RejectsOversizedCredentials()
    {
        var handler = new FakeHttpMessageHandler();
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.ResendNewDeviceOtpAsync(
            new ResendNewDeviceOtpRequest(new string('a', 257), "hash"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ResendNewDeviceOtpAsync(
            new ResendNewDeviceOtpRequest("me@example.com", new string('h', 301)),
            TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ConnectToken_AuthorizationCode_UsesPkceAndRedirectUriFormFields()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Kdf":0,"KdfIterations":600000}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.AuthorizationCode(
                "authorization-code",
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_",
                "http://localhost:8065",
                "device-1",
                "WinVaultWarden"),
            TestContext.Current.CancellationToken);

        Assert.IsType<ConnectTokenResult.Success>(result);
        var form = ParseForm(handler.Bodies[0]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("authorization-code", form["code"]);
        Assert.Equal("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_", form["code_verifier"]);
        Assert.Equal("http://localhost:8065", form["redirect_uri"]);
        Assert.Equal("desktop", form["client_id"]);
        Assert.Equal("device-1", form["deviceIdentifier"]);
        Assert.False(form.ContainsKey("username"));
        Assert.False(form.ContainsKey("password"));
    }

    [Fact]
    public async Task ConnectToken_SsoTwoFactorError_PreservesSessionContext()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["1"],"TwoFactorProviders2":{"1":{"Email":"m***@example.com"}},"SsoEmail2faSessionToken":"sso-session","Email":"me@example.com"}"""));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

        var result = await client.ConnectTokenAsync(
            ConnectTokenRequest.AuthorizationCode(
                "code",
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_",
                "http://localhost:8065",
                "device-1",
                "WinVaultWarden"),
            TestContext.Current.CancellationToken);

        var challenge = Assert.IsType<ConnectTokenResult.TwoFactorRequired>(result);
        Assert.Equal([1], challenge.Providers);
        Assert.Equal("sso-session", challenge.SsoEmail2FaSessionToken);
        Assert.Equal("me@example.com", challenge.Email);
        Assert.Equal("m***@example.com", challenge.ProviderData!["1"]!.Value.GetProperty("Email").GetString());
    }

    [Fact]
    public async Task SendTwoFactorEmailLogin_SsoContext_UsesSessionToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example/"));

        await client.SendTwoFactorEmailLoginAsync(
            new SendEmailLoginRequest(
                "me@example.com",
                string.Empty,
                "device-1",
                "sso-session"),
            TestContext.Current.CancellationToken);

        Assert.Contains("\"email\":\"me@example.com\"", handler.Bodies[0]);
        Assert.Contains("\"masterPasswordHash\":\"\"", handler.Bodies[0]);
        Assert.Contains("\"deviceIdentifier\":\"device-1\"", handler.Bodies[0]);
        Assert.Contains("\"ssoEmail2FaSessionToken\":\"sso-session\"", handler.Bodies[0]);
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => WebUtility.UrlDecode(pair[0]),
                pair => WebUtility.UrlDecode(pair[1]));
}
