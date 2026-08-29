using System.Net;
using System.Text;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class AuthServiceTests
{
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const int Iterations = 600_000;
    private const string CustomSalt = " Case-Sensitive Salt ";

    private readonly CryptoService _crypto = new();

    [Fact]
    public async Task LoginAsync_Pbkdf2Success_SavesSessionAndBootstrapsVault()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(AccessToken(), session.AccessToken);
        Assert.Equal("refresh", session.RefreshToken);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal("me@example.com", session.Account.Email);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(PersistedSession.CurrentSchemaVersion, persisted.SchemaVersion);
        Assert.Equal("https://vault.example/api", persisted.Endpoints!.Api.Value);
        Assert.Equal("refresh", persisted.RefreshToken);
        Assert.Equal(fixture.ProtectedUserKey, persisted.ProtectedUserKey);

        var form = ParseForm(handler.Bodies[1]);
        Assert.Equal("password", form["grant_type"]);
        Assert.Equal("desktop", form["client_id"]);
        Assert.Equal("6", form["deviceType"]);
        Assert.Equal(Email, form["username"]);
        Assert.Equal(fixture.PasswordHash, form["password"]);
    }

    [Fact]
    public async Task LoginAsync_OfficialNestedKdf_PreservesCustomSaltAndNormalizesUsername()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            $$"""{"kdfSettings":{"kdfType":0,"iterations":{{Iterations}},"memory":null,"parallelism":null},"salt":"{{CustomSalt}}"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.LoginAsync(
            "https://vault.example",
            " ME@EXAMPLE.COM ",
            Password,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(Email, persisted.Email);
        Assert.Equal(CustomSalt, persisted.MasterPasswordSalt);
        Assert.Equal(Email, ParseForm(handler.Bodies[1])["username"]);
        Assert.Equal(fixture.PasswordHash, ParseForm(handler.Bodies[1])["password"]);
    }

    [Fact]
    public async Task LoginAsync_NetworkFailure_ReturnsTypedFailureWithoutTransportDetail()
    {
        const string transportDetail = "SENSITIVE-ENDPOINT-DETAIL";
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(transportDetail)));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore());

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.Network, failure.Kind);
        Assert.DoesNotContain(transportDetail, failure.Message);
    }

    [Fact]
    public async Task LoginAsync_Timeout_ReturnsTypedFailureWithoutTimeoutDetail()
    {
        const string timeoutDetail = "SENSITIVE-TIMEOUT-DETAIL";
        var timeout = new TaskCanceledException(
            timeoutDetail,
            new RequestTimeoutException(
                RequestTimeoutPhase.ControlPlane,
                TimeSpan.FromSeconds(1)),
            new CancellationToken(canceled: true));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(timeout));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore());

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.Timeout, failure.Kind);
        Assert.DoesNotContain(timeoutDetail, failure.Message);
    }

    [Fact]
    public async Task LoginAsync_InvalidPreloginResponse_ReturnsTypedSafeFailure()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "not-json"));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore());

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.InvalidResponse, failure.Kind);
        Assert.Equal("The authentication response was invalid.", failure.Message);
    }

    [Fact]
    public async Task LoginAsync_UnknownServerError_DoesNotReturnServerMessage()
    {
        const string serverDetail = "SENSITIVE-SERVER-DETAIL";
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.TooManyRequests,
            $$"""{"message":"{{serverDetail}}"}"""));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore());

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.Unspecified, failure.Kind);
        Assert.DoesNotContain(serverDetail, failure.Message);
    }

    [Fact]
    public async Task LoginAsync_BootstrapFails_LocksSessionAndReportsFailure()
    {
        // S7:凭据成功但初始整库同步失败(此处 sync 返回 500)。不得残留已解锁的 UserKey/tokens,
        // 状态须收敛为 Locked(而非悬挂的 Error 或错误的 Unlocked)。
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Null(session.UserKey);           // 解锁密钥已清零,不残留内存
        Assert.Null(session.AccessToken);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
    }

    [Fact]
    public async Task LoginAsync_Argon2idIterationsOne_SavesSessionAndBootstrapsVault()
    {
        const int argonIterations = 1;
        var fixture = CreateArgon2idLoginFixture(argonIterations);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"kdf":1,"kdfIterations":1,"kdfMemory":64,"kdfParallelism":4}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal(4, handler.Requests.Count); // prelogin + token + sync + devices
        var form = ParseForm(handler.Bodies[1]);
        Assert.Equal(fixture.PasswordHash, form["password"]);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(KdfType.Argon2id, persisted.KdfType);
        Assert.Equal(argonIterations, persisted.KdfIterations);
    }

    [Fact]
    public async Task SubmitTwoFactorAsync_UsesPendingLoginContextAndUnlocks()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"],"TwoFactorProviders2":{"0":null}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());

        var first = await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);
        var challenge = Assert.IsType<AuthResult.TwoFactorRequired>(first);
        Assert.Equal([0], challenge.Providers);

        var second = await service.SubmitTwoFactorAsync("123456", provider: 0, TestContext.Current.CancellationToken, rememberDevice: false);

        Assert.IsType<AuthResult.Success>(second);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        var form = ParseForm(handler.Bodies[2]);
        Assert.Equal("0", form["twoFactorProvider"]);
        Assert.Equal("123456", form["twoFactorToken"]);
        Assert.Equal("0", form["twoFactorRemember"]);
        Assert.Equal(fixture.PasswordHash, form["password"]);
    }

    [Fact]
    public async Task SubmitTwoFactorAsync_NetworkFailure_PreservesPendingContextForRetry()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"]}"""));
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("SENSITIVE-ENDPOINT-DETAIL")));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());

        var challenge = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.TwoFactorRequired>(challenge);

        var failed = await service.SubmitTwoFactorAsync(
            "123456",
            provider: 0,
            TestContext.Current.CancellationToken,
            rememberDevice: false);
        var failure = Assert.IsType<AuthResult.Failure>(failed);
        Assert.Equal(AuthFailureKind.Network, failure.Kind);

        var retried = await service.SubmitTwoFactorAsync(
            "654321",
            provider: 0,
            TestContext.Current.CancellationToken,
            rememberDevice: false);

        Assert.IsType<AuthResult.Success>(retried);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal("654321", ParseForm(handler.Bodies[3])["twoFactorToken"]);
    }

    [Fact]
    public async Task SubmitTwoFactorAsync_PersistsCustomSaltFromPendingContext()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            $$"""{"kdfSettings":{"kdfType":0,"iterations":{{Iterations}}},"salt":"{{CustomSalt}}"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var tokenStore = new MemoryTokenStore();
        var service = CreateAuthService(handler, new VaultSession(), tokenStore);

        var first = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.TwoFactorRequired>(first);

        var result = await service.SubmitTwoFactorAsync(
            "123456",
            provider: 0,
            TestContext.Current.CancellationToken,
            rememberDevice: false);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(CustomSalt, persisted.MasterPasswordSalt);
        Assert.Equal(fixture.PasswordHash, ParseForm(handler.Bodies[2])["password"]);
    }

    [Fact]
    public async Task LoginAsync_WithPersistedRememberToken_ReusesDeviceAndSendsRememberPayload()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin("https://vault.example"), 1, Email, "device-remembered", "old-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null)
        {
            TwoFactorToken = "remember-jwt",
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        var form = ParseForm(handler.Bodies[1]);
        Assert.Equal("device-remembered", form["deviceIdentifier"]);
        Assert.Equal("5", form["twoFactorProvider"]);
        Assert.Equal("remember-jwt", form["twoFactorToken"]);
        Assert.Equal("0", form["twoFactorRemember"]);
    }

    [Fact]
    public async Task SubmitTwoFactor_ServerReturnsTwoFactorToken_PersistsIt()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            TokenJsonWithTwoFactorToken(fixture.ProtectedUserKey, "new-remember-jwt")));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);
        var result = await service.SubmitTwoFactorAsync("123456", provider: 0, TestContext.Current.CancellationToken, rememberDevice: true);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal("new-remember-jwt", persisted.TwoFactorToken);
    }

    [Fact]
    public async Task UnlockAsync_WithPersistedSession_RefreshesTokenAndBootstraps()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin("https://vault.example"), 1, Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal("new-access", session.AccessToken);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal("new-refresh", persisted.RefreshToken);
        Assert.Equal("refresh_token", ParseForm(handler.Bodies[0])["grant_type"]);
    }

    [Fact]
    public async Task UnlockAsync_WithCustomPersistedSalt_UsesExactSalt()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin("https://vault.example"),
            1,
            Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            CustomSalt));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
    }

    [Fact]
    public async Task UnlockAsync_WithWrongPassword_ReturnsFailureAndStaysLocked()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin("https://vault.example"), 1, Email, "device-id", "refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockAsync("wrong password", TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.InvalidCredentials, failure.Kind);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task LoginAsync_EmailTwoFactorRequired_AutoSendsLoginEmail()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["1"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}")); // send-email-login
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());

        var result = await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.TwoFactorRequired>(result);
        Assert.Equal(3, handler.Requests.Count); // prelogin + connect/token + send-email-login
        Assert.Equal("/api/two-factor/send-email-login", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.Contains("\"email\":\"me@example.com\"", handler.Bodies[2]);
    }

    [Fact]
    public async Task LoginAsync_AuthenticatorOnly_DoesNotSendEmail()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"]}"""));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());

        await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count); // 无 send-email-login
    }

    [Fact]
    public async Task ResendTwoFactorEmailAsync_AfterChallenge_PostsSendEmailLoginAgain()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["1"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}")); // auto-send on entering stage
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}")); // resend
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());

        await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);
        var sent = await service.ResendTwoFactorEmailAsync(TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("/api/two-factor/send-email-login", handler.Requests[3].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ResendTwoFactorEmailAsync_NoPendingChallenge_ReturnsFalse()
    {
        var handler = new FakeHttpMessageHandler();
        var service = CreateAuthService(handler, new VaultSession(), new MemoryTokenStore());

        Assert.False(await service.ResendTwoFactorEmailAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SubmitTwoFactorAsync_SendsSelectedProvider_NotFirst()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0","1"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}")); // auto send-email-login (email present)
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());

        await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken);
        var result = await service.SubmitTwoFactorAsync("123456", provider: 1, TestContext.Current.CancellationToken, rememberDevice: false);

        Assert.IsType<AuthResult.Success>(result);
        // Only the submission request carries twoFactorProvider; the email request is JSON.
        var submitForm = handler.Bodies
            .Where(b => b.Contains("twoFactorProvider"))
            .Select(ParseForm)
            .Single();
        Assert.Equal("1", submitForm["twoFactorProvider"]);
        Assert.Equal("123456", submitForm["twoFactorToken"]);
    }

    [Fact]
    public async Task LoginAsync_AccessTokenWithoutSub_DoesNotEstablishSession()
    {
        var fixture = CreateLoginFixture();
        var identity = new ControlledIdentityApiClient(_ =>
            Task.FromResult<ConnectTokenResult>(
                new ConnectTokenResult.Success(new TokenResponse(
                    "not-a-jwt",
                    "refresh-b",
                    3600,
                    "Bearer",
                    "api offline_access",
                    fixture.ProtectedUserKey,
                    null,
                    KdfType.Pbkdf2,
                    600_000))));
        var session = new VaultSession();
        var store = new MemoryTokenStore();
        var service = CreateAuthService(identity, session, store);

        var result = await service.LoginAsync(
            "https://b.example", Email, Password, TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.InvalidResponse, failure.Kind);
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
        Assert.False(store.TryLoad(out _));
    }

    private AuthService CreateAuthService(FakeHttpMessageHandler handler, VaultSession session, MemoryTokenStore tokenStore)
    {
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var provider = new FakeVaultApiClientProvider().AddAnonymous<IIdentityApiClient>(api).AddAuthenticated<IReadonlyApiClient>(api);
        var bootstrapper = new VaultBootstrapper(new SyncService(provider, decryptor, session, cache), provider, session);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        return new AuthService(provider, _crypto, session, tokenStore, bootstrapper, cache, refresher);
    }

    private AuthService CreateAuthService(IIdentityApiClient identity, VaultSession session, MemoryTokenStore tokenStore)
    {
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var provider = FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        return new AuthService(provider, _crypto, session, tokenStore, bootstrapper: null!, cache, refresher);
    }

    private LoginFixture CreateLoginFixture(string salt = Email)
    {
        var masterKey = _crypto.DeriveMasterKey(Password, salt, KdfType.Pbkdf2, Iterations, null, null);
        var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, Password);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        return new LoginFixture(userKey, protectedUserKey, passwordHash);
    }

    private LoginFixture CreateArgon2idLoginFixture(int iterations)
    {
        var masterKey = _crypto.DeriveMasterKey(Password, Email, KdfType.Argon2id, iterations, 64, 4);
        var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, Password);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        return new LoginFixture(userKey, protectedUserKey, passwordHash);
    }

    private static string PreloginJson() =>
        $$"""{"kdf":0,"kdfIterations":{{Iterations}},"kdfMemory":null,"kdfParallelism":null}""";

    // 生成带真实 sub 的三段 JWT(alg=none 仅测试用);AccountId 由 CompleteLoginAsync 从此 sub 派生。
    private static string AccessToken(string accountId = Email)
    {
        static string Base64Url(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        var header = Base64Url("""{"alg":"none"}""");
        var payload = Base64Url($$"""{"sub":"{{accountId}}"}""");
        return $"{header}.{payload}.signature";
    }

    private static string TokenJson(string protectedUserKey) =>
        $$"""{"access_token":"{{AccessToken()}}","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"{{protectedUserKey}}","PrivateKey":null,"Kdf":0,"KdfIterations":{{Iterations}}}""";

    private static string TokenJsonWithTwoFactorToken(string protectedUserKey, string twoFactorToken) =>
        $$"""{"access_token":"{{AccessToken()}}","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"{{protectedUserKey}}","PrivateKey":null,"Kdf":0,"KdfIterations":{{Iterations}},"TwoFactorToken":"{{twoFactorToken}}"}""";

    private static string SyncJson() =>
        """{"object":"sync","profile":{"id":"me@example.com","email":"me@example.com","name":"Me"},"folders":[],"ciphers":[]}""";

    private static string DevicesJson() =>
        """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}""";

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    private sealed record LoginFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey, string PasswordHash);
}
