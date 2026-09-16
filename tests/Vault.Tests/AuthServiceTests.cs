using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public class AuthServiceTests
{
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const int Iterations = 600_000;
    private const string CustomSalt = " Case-Sensitive Salt ";
    private const string SsoVerifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";

    private readonly CryptoService _crypto = new();

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AuthFailureKind.InvalidCredentials)]
    [InlineData(HttpStatusCode.Unauthorized, AuthFailureKind.InvalidCredentials)]
    [InlineData(HttpStatusCode.TooManyRequests, AuthFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AuthFailureKind.Unspecified)]
    public async Task LoginAsync_TokenFailurePreservesStatusWithoutEstablishingSession(
        HttpStatusCode status,
        AuthFailureKind expectedKind)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(status,
            """{"error":"invalid_grant","error_description":"sensitive server diagnostic"}"""));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.LoginAsync(
            "https://vault.example", Email, Password, TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(expectedKind, failure.Kind);
        Assert.DoesNotContain("sensitive server diagnostic", failure.Message, StringComparison.Ordinal);
        Assert.Null(session.UserKey);
        Assert.Null(session.AccessToken);
        Assert.False(tokenStore.TryLoad(out _));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SubmitTwoFactorAsync_RateLimitedPreservesChallengeForExplicitRetry()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.TooManyRequests,
            """{"error":"invalid_grant","error_description":"sensitive server diagnostic"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        Assert.IsType<AuthResult.TwoFactorRequired>(await service.LoginAsync(
            "https://vault.example", Email, Password, TestContext.Current.CancellationToken));
        var limited = await service.SubmitTwoFactorAsync(
            "123456", provider: 0, TestContext.Current.CancellationToken);

        Assert.Equal(AuthFailureKind.RateLimited, Assert.IsType<AuthResult.Failure>(limited).Kind);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(tokenStore.TryLoad(out _));
        Assert.Null(session.UserKey);

        var retried = await service.SubmitTwoFactorAsync(
            "654321", provider: 0, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(retried);
        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal("654321", ParseForm(handler.Bodies[3])["twoFactorToken"]);
    }

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
        Assert.True(persisted.HasMasterPassword);
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
    public async Task LoginAsync_FinalTokenRequestWaitsForDeletionCoordination()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var gate = new PausingAccountDeletionCoordinationGate();
        var store = new MemoryTokenStore();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            accountDeletionCoordinationGate: gate);

        var login = service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);
        await gate.AcquireStarted.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.False(store.TryLoad(out _));
        gate.Release();
        Assert.IsType<AuthResult.Success>(await login);
        Assert.True(store.TryLoad(out _));
        Assert.Equal(1, gate.DisposeCount);
    }

    [Fact]
    public async Task LoginAsync_OfficialNestedKdf_PreservesCustomSaltAndNormalizesUsername()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            $$"""{"kdfSettings":{"kdfType":0,"iterations":{{Iterations}},"memory":null,"parallelism":null},"salt":"{{CustomSalt}}"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey).Replace(
                $"\"KdfIterations\":{Iterations}",
                $"\"KdfIterations\":{Iterations},\"UserDecryptionOptions\":{{\"HasMasterPassword\":true}}",
                StringComparison.Ordinal)));
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
        Assert.True(persisted.HasMasterPassword);
        Assert.Equal(Email, ParseForm(handler.Bodies[1])["username"]);
        Assert.Equal(fixture.PasswordHash, ParseForm(handler.Bodies[1])["password"]);
    }

    [Fact]
    public async Task FreshPasswordLogin_PendingAccountDeletionDoesNotEstablishSession()
    {
        var fixture = CreateLoginFixture();
        var bindingSession = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            ServerEndpoints.FromWebVault("https://vault.example"),
            1,
            Email,
            "11111111-1111-1111-1111-111111111111",
            "unused-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            Email);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            tokenStore,
            accountDeletionTombstones:
                FixedAccountDeletionTombstoneStore.ForSession(bindingSession));

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.AccountDeletionRecoveryRequired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.False(tokenStore.TryLoad(out _));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FreshSsoLogin_PendingAccountDeletionDoesNotEstablishSession()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var bindingSession = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            ServerEndpoints.FromWebVault("https://vault.example"),
            1,
            Email,
            "11111111-1111-1111-1111-111111111111",
            "unused-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            Email);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SsoTokenJson(fixture.ProtectedUserKey, CustomSalt)));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            tokenStore,
            accountDeletionTombstones:
                FixedAccountDeletionTombstoneStore.ForSession(bindingSession));

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.AccountDeletionRecoveryRequired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.False(tokenStore.TryLoad(out _));
        Assert.Single(handler.Requests);
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
    public async Task LoginAsync_RateLimited_DoesNotReturnServerMessage()
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
        Assert.Equal(AuthFailureKind.RateLimited, failure.Kind);
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
    public async Task LoginAsync_NewDeviceVerification_ReplaysBoundPasswordGrantAndUnlocks()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"New device verification required","ErrorModel":{"Message":"new device verification required"}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var attempt = service.BeginAuthenticationAttempt();

        var first = await service.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);
        var second = await service.SubmitNewDeviceVerificationAsync(
            attempt,
            " 123456 ",
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.DeviceVerificationRequired>(first);
        Assert.IsType<AuthResult.Success>(second);
        var initial = ParseForm(handler.Bodies[1]);
        var verified = ParseForm(handler.Bodies[2]);
        Assert.Equal(initial["username"], verified["username"]);
        Assert.Equal(initial["password"], verified["password"]);
        Assert.Equal(initial["deviceIdentifier"], verified["deviceIdentifier"]);
        Assert.Equal("123456", verified["newDeviceOtp"]);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
    }

    [Fact]
    public async Task SubmitNewDeviceVerification_InvalidOtpPreservesAttemptForRetry()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"New device verification required","ErrorModel":{"Message":"new device verification required"}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"Invalid New Device OTP","ErrorModel":{"Message":"invalid new device otp"}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var attempt = service.BeginAuthenticationAttempt();

        Assert.IsType<AuthResult.DeviceVerificationRequired>(await service.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));
        var rejected = await service.SubmitNewDeviceVerificationAsync(
            attempt,
            "000000",
            TestContext.Current.CancellationToken);
        var accepted = await service.SubmitNewDeviceVerificationAsync(
            attempt,
            "123456",
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthFailureKind.InvalidCredentials, Assert.IsType<AuthResult.Failure>(rejected).Kind);
        Assert.IsType<AuthResult.Success>(accepted);
        Assert.Equal("000000", ParseForm(handler.Bodies[2])["newDeviceOtp"]);
        Assert.Equal("123456", ParseForm(handler.Bodies[3])["newDeviceOtp"]);
    }

    [Fact]
    public async Task SubmitNewDeviceVerification_CanContinueIntoTwoFactor()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"New device verification required","ErrorModel":{"Message":"new device verification required"}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"],"TwoFactorProviders2":{"0":null}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var service = CreateAuthService(handler, new VaultSession(), new MemoryTokenStore());
        var attempt = service.BeginAuthenticationAttempt();

        Assert.IsType<AuthResult.DeviceVerificationRequired>(await service.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));
        var verification = await service.SubmitNewDeviceVerificationAsync(
            attempt,
            "123456",
            TestContext.Current.CancellationToken);
        var completed = await service.SubmitTwoFactorAsync(
            attempt,
            "654321",
            provider: 0,
            TestContext.Current.CancellationToken,
            rememberDevice: false);

        Assert.Equal([0], Assert.IsType<AuthResult.TwoFactorRequired>(verification).Providers);
        Assert.IsType<AuthResult.Success>(completed);
        Assert.Equal("123456", ParseForm(handler.Bodies[2])["newDeviceOtp"]);
        Assert.Equal("654321", ParseForm(handler.Bodies[3])["twoFactorToken"]);
        Assert.False(ParseForm(handler.Bodies[3]).ContainsKey("newDeviceOtp"));
    }

    [Fact]
    public async Task ResendNewDeviceVerification_UsesBoundEmailAndPasswordHash()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"New device verification required","ErrorModel":{"Message":"new device verification required"}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.NoContent, string.Empty));
        var service = CreateAuthService(handler, new VaultSession(), new MemoryTokenStore());
        var attempt = service.BeginAuthenticationAttempt();

        Assert.IsType<AuthResult.DeviceVerificationRequired>(await service.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));
        var sent = await service.ResendNewDeviceVerificationAsync(
            attempt,
            TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.Equal("/api/accounts/resend-new-device-otp", handler.Requests[2].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal(Email, body.RootElement.GetProperty("email").GetString());
        Assert.Equal(fixture.PasswordHash, body.RootElement.GetProperty("masterPasswordHash").GetString());
    }

    [Fact]
    public async Task NewDeviceVerification_ContextExpiresAfterFiveMinutes()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"New device verification required","ErrorModel":{"Message":"new device verification required"}}"""));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore(),
            timeProvider: clock);
        var attempt = service.BeginAuthenticationAttempt();

        Assert.IsType<AuthResult.DeviceVerificationRequired>(await service.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromMinutes(5));

        var result = await service.SubmitNewDeviceVerificationAsync(
            attempt,
            "123456",
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthFailureKind.SessionExpired, Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Equal(2, handler.Requests.Count);
        Assert.False(await service.ResendNewDeviceVerificationAsync(
            attempt,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NewDeviceVerification_ActivelyDisposesAuthenticationAttemptAtExpiry()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"device_error","error_description":"New device verification required","ErrorModel":{"Message":"new device verification required"}}"""));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore(),
            newDeviceVerificationLifetime: TimeSpan.FromMilliseconds(30));
        var attempt = service.BeginAuthenticationAttempt();

        Assert.IsType<AuthResult.DeviceVerificationRequired>(await service.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));
        await Task.Delay(250, TestContext.Current.CancellationToken);

        Assert.True(attempt.CancellationToken.IsCancellationRequested);
        var expired = await service.SubmitNewDeviceVerificationAsync(
            attempt,
            "123456",
            TestContext.Current.CancellationToken);
        Assert.Equal(AuthFailureKind.SessionExpired, Assert.IsType<AuthResult.Failure>(expired).Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LoginAsync_AdminForcePasswordReset_EntersRestrictedSessionWithoutBootstrapOrPersistence()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 16,
                enforceOnLogin: true)));
        var store = new MemoryTokenStore();
        store.Save(SavedSession(Email, "https://vault.example") with
        {
            ProtectedUserKey = fixture.ProtectedUserKey,
        });
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store);

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(result);
        Assert.Equal(RequiredPasswordChangeReason.AdministratorTemporaryPassword, required.Reason);
        Assert.Equal(16, required.Policy!.MinLength);
        Assert.Equal(VaultState.PasswordChangeRequired, session.State);
        Assert.Null(session.UserKey);
        Assert.Equal(AccessToken(), session.AccessToken);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LoginAsync_AdminForcePasswordResetRemovalFailureDoesNotPublishRestrictedSession()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 16,
                enforceOnLogin: true)));
        var store = new FailingRemovalTokenStore(
            SavedSession(Email, "https://vault.example") with
            {
                ProtectedUserKey = fixture.ProtectedUserKey,
            });
        var cacheStore = new FakeCacheStore();
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            store,
            cacheStore: cacheStore);

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Contains("saved session could not be removed", failure.Message);
        Assert.NotEqual(VaultState.PasswordChangeRequired, session.State);
        Assert.Null(session.UserKey);
        Assert.True(store.RemoveCalled);
        Assert.True(store.TryLoad(out _));
        Assert.Equal(1, cacheStore.ClearCount);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LoginAsync_StaleForcedPasswordAttemptDoesNotDeleteReplacementSession()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 16,
                enforceOnLogin: true)));
        var crypto = new BlockingDecryptCryptoService();
        var store = new MemoryTokenStore();
        var cacheStore = new FakeCacheStore();
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            store,
            cacheStore: cacheStore,
            crypto: crypto);
        var staleAttempt = service.BeginAuthenticationAttempt();
        var loginTask = Task.Run(() => service.LoginAsync(
            staleAttempt,
            "https://vault.example",
            Email,
            Password,
            CancellationToken.None));

        AuthenticationAttemptLease? replacementAttempt = null;
        try
        {
            await crypto.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            replacementAttempt = service.BeginAuthenticationAttempt();
            store.Save(SavedSession(Email, "https://vault.example") with
            {
                RefreshToken = "replacement-refresh-token",
                ProtectedUserKey = fixture.ProtectedUserKey,
            });
        }
        finally
        {
            crypto.Release();
        }

        var result = await loginTask.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.SessionExpired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.True(store.TryLoad(out var replacement));
        Assert.Equal("replacement-refresh-token", replacement.RefreshToken);
        Assert.Equal(1, cacheStore.ClearCount);
        if (replacementAttempt is not null)
            service.AbandonAuthenticationAttempt(replacementAttempt);
    }

    [Fact]
    public async Task CompleteRequiredPasswordChange_AdminUsesUpdateTempAndEndsRestrictedSession()
    {
        const string nextPassword = "Correct-Horse-Battery-Staple#42";
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 16,
                enforceOnLogin: true)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.NoContent, string.Empty));
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store);
        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        var changed = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            nextPassword,
            "new hint",
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.PasswordChanged>(changed);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);
        Assert.Equal("/api/accounts/update-temp-password", handler.Requests[2].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Bodies[2]);
        Assert.False(body.RootElement.TryGetProperty("masterPasswordHash", out _));
        Assert.Equal(
            body.RootElement.GetProperty("newMasterPasswordHash").GetString(),
            body.RootElement.GetProperty("authenticationData")
                .GetProperty("masterPasswordAuthenticationHash")
                .GetString());
        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.Null(session.AccessToken);
        Assert.False(store.TryLoad(out _));
    }

    [Fact]
    public async Task LoginAsync_EnforcedWeakPasswordPolicy_UsesNormalPasswordChangeEndpoint()
    {
        const string nextPassword = "Correct-Horse-Battery-Staple#42";
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: false,
                minLength: 30,
                enforceOnLogin: true)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.NoContent, string.Empty));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        Assert.Equal(RequiredPasswordChangeReason.WeakMasterPassword, required.Reason);
        var changed = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            nextPassword,
            hint: null,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.PasswordChanged>(changed);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal("/api/accounts/password", handler.Requests[2].RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Bodies[2]);
        Assert.Equal(fixture.PasswordHash, body.RootElement.GetProperty("masterPasswordHash").GetString());
    }

    [Fact]
    public async Task CompleteRequiredPasswordChange_RejectsPolicyViolationBeforeNetworkWrite()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 20,
                enforceOnLogin: true,
                requireSpecial: true)));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        var rejected = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "NoSpecialCharacter123456",
            hint: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthFailureKind.SafeMessage, Assert.IsType<AuthResult.Failure>(rejected).Kind);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(VaultState.PasswordChangeRequired, session.State);
        Assert.False(required.Lease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task LoginAsync_AdminForceReasonWinsOverWeakPasswordReason()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 128,
                enforceOnLogin: true)));
        var service = CreateAuthService(handler, new VaultSession(), new MemoryTokenStore());

        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        Assert.Equal(RequiredPasswordChangeReason.AdministratorTemporaryPassword, required.Reason);
    }

    [Fact]
    public async Task LoginAsync_MalformedMasterPasswordPolicyFailsBeforeSessionMutation()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: false,
                minLength: 11,
                enforceOnLogin: true)));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthFailureKind.InvalidResponse, Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.Null(session.AccessToken);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CompleteRequiredPasswordChange_TransportFailureEndsSessionWithoutBlindRetry()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 12,
                enforceOnLogin: false)));
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("response lost")));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        var uncertain = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "New-Master-Password#42",
            hint: null,
            TestContext.Current.CancellationToken);
        var retry = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "New-Master-Password#42",
            hint: null,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.PasswordChangeUncertain>(uncertain);
        Assert.Equal(AuthFailureKind.SessionExpired, Assert.IsType<AuthResult.Failure>(retry).Kind);
        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.Null(session.AccessToken);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CompleteRequiredPasswordChange_ConcurrentSubmissionSendsOnlyOneWrite()
    {
        var fixture = CreateLoginFixture();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 12,
                enforceOnLogin: false)));
        handler.Enqueue(async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return FakeHttpMessageHandler.Json(HttpStatusCode.NoContent, string.Empty);
        });
        var service = CreateAuthService(handler, new VaultSession(), new MemoryTokenStore());
        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        var first = service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "New-Master-Password#42",
            hint: null,
            TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "Other-Master-Password#43",
            hint: null,
            TestContext.Current.CancellationToken);
        release.TrySetResult();

        Assert.Equal(AuthFailureKind.SessionExpired, Assert.IsType<AuthResult.Failure>(concurrent).Kind);
        Assert.IsType<AuthResult.PasswordChanged>(await first);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CompleteRequiredPasswordChange_ServerErrorIsTreatedAsUncertain()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 12,
                enforceOnLogin: false)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.InternalServerError,
            """{"message":"post-commit side effect failed"}"""));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        var result = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "New-Master-Password#42",
            hint: null,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.PasswordChangeUncertain>(result);
        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.True(required.Lease.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CompleteRequiredPasswordChange_BadRequestRemainsRetryable()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            RequiredPasswordTokenJson(
                fixture.ProtectedUserKey,
                forcePasswordReset: true,
                minLength: 12,
                enforceOnLogin: false)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"message":"password rejected"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.NoContent, string.Empty));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var required = Assert.IsType<AuthResult.PasswordChangeRequired>(await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        var rejected = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "New-Master-Password#42",
            hint: null,
            TestContext.Current.CancellationToken);
        var accepted = await service.CompleteRequiredPasswordChangeAsync(
            required.Lease,
            "Other-Master-Password#43",
            hint: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(AuthFailureKind.SafeMessage, Assert.IsType<AuthResult.Failure>(rejected).Kind);
        Assert.IsType<AuthResult.PasswordChanged>(accepted);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(VaultState.LoggedOut, session.State);
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
    public async Task LoginAsync_WebAuthnChallenge_PreservesProviderData()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["7"],"TwoFactorProviders2":{"7":{"challenge":"AAECAwQFBgcICQoLDA0ODw","rpId":"vault.example","allowCredentials":[{"type":"public-key","id":"AQID"}],"userVerification":"discouraged"}}}"""));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore());

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var challenge = Assert.IsType<AuthResult.TwoFactorRequired>(result);
        var providerJson = challenge.ProviderData![(int)TwoFactorProviderType.WebAuthn];
        using var document = JsonDocument.Parse(providerJson);
        Assert.Equal("vault.example", document.RootElement.GetProperty("rpId").GetString());
    }

    [Theory]
    [InlineData(3, "cccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData(8, "recovery-code-value")]
    public async Task SubmitTwoFactorAsync_ForwardsYubiKeyAndRecoveryProviders(
        int provider,
        string code)
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["{{(provider == 8 ? 7 : provider)}}"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var service = CreateAuthService(handler, new VaultSession(), new MemoryTokenStore());

        Assert.IsType<AuthResult.TwoFactorRequired>(
            await service.LoginAsync("https://vault.example", Email, Password, TestContext.Current.CancellationToken));
        Assert.IsType<AuthResult.Success>(
            await service.SubmitTwoFactorAsync(
                code,
                provider,
                TestContext.Current.CancellationToken,
                rememberDevice: false));

        var form = ParseForm(handler.Bodies[2]);
        Assert.Equal(provider.ToString(), form["twoFactorProvider"]);
        Assert.Equal(code, form["twoFactorToken"]);
        Assert.Equal("0", form["twoFactorRemember"]);
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

    [Theory]
    [InlineData((int)TwoFactorProviderType.Duo)]
    [InlineData((int)TwoFactorProviderType.OrganizationDuo)]
    [InlineData((int)TwoFactorProviderType.WebAuthn)]
    public async Task SubmitSingleUseTwoFactorAsync_UncertainFailureExpiresPendingContext(
        int provider)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["{{provider}}"]}"""));
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("SENSITIVE-ENDPOINT-DETAIL")));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var attempt = service.BeginAuthenticationAttempt();

        Assert.IsType<AuthResult.TwoFactorRequired>(await service.LoginAsync(
            attempt,
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken));

        var failed = await service.SubmitTwoFactorAsync(
            attempt,
            "single-use-token",
            provider,
            TestContext.Current.CancellationToken,
            rememberDevice: false);

        var failure = Assert.IsType<AuthResult.Failure>(failed);
        Assert.Equal(AuthFailureKind.SessionExpired, failure.Kind);
        Assert.False(session.IsCurrent(attempt));
        Assert.IsType<AuthResult.Failure>(await service.SubmitTwoFactorAsync(
            attempt,
            "same-token",
            provider,
            TestContext.Current.CancellationToken,
            rememberDevice: false));
        Assert.Equal(3, handler.Requests.Count);
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
    public async Task LoginAsync_StableInstallationIdentifier_DoesNotReuseMismatchedRememberToken()
    {
        const string stableDevice = "11111111-2222-3333-4444-555555555555";
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin("https://vault.example"),
            1,
            Email,
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "old-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            TwoFactorToken = "old-device-remember-token",
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, PreloginJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            tokenStore,
            deviceIdentifierProvider: new FixedDeviceIdentifierProvider(stableDevice));

        var result = await service.LoginAsync(
            "https://vault.example",
            Email,
            Password,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        var form = ParseForm(handler.Bodies[1]);
        Assert.Equal(stableDevice, form["deviceIdentifier"]);
        Assert.DoesNotContain("twoFactorProvider", form.Keys);
        Assert.DoesNotContain("twoFactorToken", form.Keys);
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
            $$"""{"access_token":"{{AccessToken()}}","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(AccessToken(), session.AccessToken);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal("new-refresh", persisted.RefreshToken);
        Assert.Equal("refresh_token", ParseForm(handler.Bodies[0])["grant_type"]);
    }

    [Fact]
    public async Task UnlockAsync_NestedRefreshReentersTheSameDeletionCoordinationFlow()
    {
        var fixture = CreateLoginFixture();
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
            null));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken()}}","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var coordination = new AmbientTrackingAccountDeletionCoordinationGate();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            tokenStore,
            accountDeletionCoordinationGate: coordination);

        var result = await service.UnlockAsync(
            Password,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(1, coordination.OuterAcquisitionCount);
        Assert.Equal(1, coordination.ReentrantAcquisitionCount);
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
            $$"""{"access_token":"{{AccessToken()}}","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
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

    [Fact]
    public async Task LoginWithSsoAsync_MasterPasswordAccount_PersistsLockedSessionThenUnlocks()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SsoTokenJson(fixture.ProtectedUserKey, CustomSalt)));
        var tokenStore = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);
        var attempt = service.BeginAuthenticationAttempt();

        var authenticated = await service.LoginWithSsoAsync(
            attempt,
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.SsoAuthenticated>(authenticated);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(CustomSalt, persisted.MasterPasswordSalt);
        Assert.Equal(fixture.ProtectedUserKey, persisted.ProtectedUserKey);
        Assert.True(persisted.HasMasterPassword);
        Assert.Single(handler.Requests);
        var ssoForm = ParseForm(handler.Bodies[0]);
        Assert.Equal("authorization_code", ssoForm["grant_type"]);
        Assert.Equal("sso-code", ssoForm["code"]);
        Assert.Equal(SsoVerifier, ssoForm["code_verifier"]);
        Assert.Equal("http://localhost:8065", ssoForm["redirect_uri"]);

        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken()}}","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));

        var unlocked = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(unlocked);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal(Core.Session.VaultState.Unlocked, session.State);
    }

    [Fact]
    public async Task LoginWithSsoAsync_ForcePasswordResetRemovesPriorUnlockArtifacts()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var response = SsoTokenJson(fixture.ProtectedUserKey, CustomSalt).Replace(
            "\"UserDecryptionOptions\"",
            "\"ForcePasswordReset\":true,\"UserDecryptionOptions\"",
            StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var store = new MemoryTokenStore();
        store.Save(SavedSession(Email, "https://vault.example") with
        {
            ProtectedUserKey = fixture.ProtectedUserKey,
            PinProtectedUserKey = "2.pin",
            HelloProtectedUserKey = "2.hello",
        });
        var cacheStore = new FakeCacheStore();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            cacheStore: cacheStore);
        var attempt = service.BeginAuthenticationAttempt();

        var result = await service.LoginWithSsoAsync(
            attempt,
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(1, cacheStore.ClearCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoginWithSsoAsync_ForcePasswordResetRemovalFailureIsReported()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var response = SsoTokenJson(fixture.ProtectedUserKey, CustomSalt).Replace(
            "\"UserDecryptionOptions\"",
            "\"ForcePasswordReset\":true,\"UserDecryptionOptions\"",
            StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var store = new FailingRemovalTokenStore(
            SavedSession(Email, "https://vault.example") with
            {
                ProtectedUserKey = fixture.ProtectedUserKey,
            });
        var cacheStore = new FakeCacheStore();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            cacheStore: cacheStore);
        var attempt = service.BeginAuthenticationAttempt();

        var result = await service.LoginWithSsoAsync(
            attempt,
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Contains("saved session could not be removed", failure.Message);
        Assert.True(store.RemoveCalled);
        Assert.True(store.TryLoad(out _));
        Assert.Equal(1, cacheStore.ClearCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoginWithSsoAsync_MalformedV2AccountKeysFailClosedBeforeFallback()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var response = SsoTokenJson(fixture.ProtectedUserKey, CustomSalt)
            .Replace(
                "\"PrivateKey\":null,",
                "\"PrivateKey\":null," + V2AccountKeysJson() + ",",
                StringComparison.Ordinal)
            .Replace(
                "\"UserDecryptionOptions\":{\"HasMasterPassword\":true,",
                "\"UserDecryptionOptions\":{\"HasMasterPassword\":true,\"TrustedDeviceOption\":{"
                    + "\"HasAdminApproval\":true,\"HasLoginApprovingDevice\":true,"
                    + "\"HasManageResetPasswordPermission\":false,\"IsTdeOffboarding\":false,"
                    + "\"EncryptedPrivateKey\":\"2.AA==|AA==|AA==\",\"EncryptedUserKey\":\"4.AA==\"},",
                StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var store = new MemoryTokenStore();
        var deviceTrust = new FakeDeviceTrustService(new byte[64]);
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            deviceTrust: deviceTrust);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.InvalidResponse, failure.Kind);
        Assert.False(store.TryLoad(out _));
        Assert.Null(deviceTrust.DeviceIdentifier);
        Assert.Equal(0, deviceTrust.RecordTrustLossCount);
    }

    [Fact]
    public async Task LoginWithSsoAsync_LegacyResponse_UsesPreloginForUnlockMaterial()
    {
        var fixture = CreateLoginFixture(CustomSalt);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TokenJson(fixture.ProtectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"kdfSettings":{"kdfType":0,"iterations":{{Iterations}}},"salt":"{{CustomSalt}}"}"""));
        var store = new MemoryTokenStore();
        var service = CreateAuthService(handler, new VaultSession(), store);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.SsoAuthenticated>(result);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal(CustomSalt, persisted.MasterPasswordSalt);
        Assert.Null(persisted.HasMasterPassword);
        Assert.Equal("/identity/connect/token", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/identity/accounts/prelogin", handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task LoginWithSsoAsync_UnsupportedDecryptionMode_DoesNotPersistSession()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$$$"""{"access_token":"{{{{AccessToken()}}}}","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Kdf":0,"KdfIterations":{{{{Iterations}}}},"UserDecryptionOptions":{"HasMasterPassword":false,"TrustedDeviceOption":{"EncryptedUserKey":"2.key"}}}"""));
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoginWithSsoAsync_TdeJitOptionFreezesOrganizationIdentifierAndRequiresConfirmation()
    {
        const string accountId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$$$"""{"access_token":"{{{{AccessToken(accountId)}}}}","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Kdf":0,"KdfIterations":{{{{Iterations}}}},"UserDecryptionOptions":{"HasMasterPassword":false,"TrustedDeviceOption":{"HasAdminApproval":false,"HasLoginApprovingDevice":false,"HasManageResetPasswordPermission":false,"IsTdeOffboarding":false}}}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"version":"2026.8.0","featureStates":{"pm-27279-v2-registration-tde-jit":true}}"""));
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var jit = Assert.IsType<AuthResult.TdeV2JitProvisioningRequired>(result);
        Assert.Equal("organization", jit.OrganizationIdentifier);
        Assert.Equal(accountId, jit.Lease.AccountId);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.False(session.UsesV2UserKey);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal(PersistedSessionUnlockMethod.TrustedDeviceSso, persisted.UnlockMethod);
        Assert.Equal(string.Empty, persisted.ProtectedUserKey);
        Assert.False(persisted.HasMasterPassword);
    }

    [Fact]
    public async Task LoginWithSsoAsync_IncompleteTrustedDeviceOptionCannotStartTdeJit()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$$$"""{"access_token":"{{{{AccessToken()}}}}","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Kdf":0,"KdfIterations":{{{{Iterations}}}},"UserDecryptionOptions":{"HasMasterPassword":false,"TrustedDeviceOption":{}}}"""));
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoginWithSsoAsync_UnsafeKdfParameters_DoesNotPersistSession()
    {
        var fixture = CreateLoginFixture();
        var unsafeToken = SsoTokenJson(fixture.ProtectedUserKey, Email)
            .Replace($"\"Iterations\":{Iterations}", "\"Iterations\":99", StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, unsafeToken));
        var store = new MemoryTokenStore();
        var service = CreateAuthService(handler, new VaultSession(), store);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.InvalidResponse, failure.Kind);
        Assert.False(store.TryLoad(out _));
    }

    [Fact]
    public async Task KeyConnectorSso_LoginAndSubsequentUnlockFetchMasterKey()
    {
        var masterKeyBytes = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        using var stretchedKey = _crypto.StretchMasterKey(masterKeyBytes);
        using var expectedUserKey = new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(value => (byte)value).ToArray());
        var wrappedUserKey = _crypto.Encrypt(expectedUserKey.FullKey, stretchedKey);
        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, wrappedUserKey.Type);
        var protectedUserKey = wrappedUserKey.ToString();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            KeyConnectorTokenJson(protectedUserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var connector = new FakeKeyConnectorClient(masterKeyBytes);
        var deviceTrust = new FakeDeviceTrustService(new byte[64]);
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store, connector, deviceTrust);

        var login = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.True(
            login is AuthResult.Success,
            login is AuthResult.Failure failure
                ? $"Login failed: {failure.Kind}: {failure.Message}; Key Connector calls: {connector.Calls.Count}; HTTP requests: {string.Join(", ", handler.Requests.Select(request => request.RequestUri?.AbsolutePath))}"
                : $"Unexpected login result: {login.GetType().Name}");
        Assert.Equal(expectedUserKey.FullKey, session.UserKey!.FullKey);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal(PersistedSessionUnlockMethod.KeyConnector, persisted.UnlockMethod);
        Assert.Equal("https://keys.example/base", persisted.KeyConnectorUrl);
        Assert.False(persisted.HasMasterPassword);
        Assert.Single(connector.Calls);

        await service.LockAsync(TestContext.Current.CancellationToken);
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken()}}","refresh_token":"refreshed-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));

        var unlock = await service.UnlockWithKeyConnectorAsync(
            service.BeginAuthenticationAttempt(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(unlock);
        Assert.Equal(expectedUserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal(2, connector.Calls.Count);
        Assert.Equal(AccessToken(), connector.Calls[1].AccessToken);
        Assert.Equal(2, deviceTrust.CapabilitySetCount);
        Assert.False(deviceTrust.CapabilitySupported);
    }

    [Fact]
    public async Task KeyConnectorSso_V2AccountKeysFailsClosedBeforeKeyRetrieval()
    {
        var keyConnectorKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        using var stretchedKey = _crypto.StretchMasterKey(keyConnectorKey);
        using var userKey = new SymmetricCryptoKey(new byte[64]);
        var wrappedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        var response = KeyConnectorTokenJson(wrappedUserKey).Replace(
            "\"PrivateKey\":null,",
            "\"PrivateKey\":null,\"AccountKeys\":{"
                + "\"publicKeyEncryptionKeyPair\":{\"wrappedPrivateKey\":\"7.AA==\"},"
                + "\"signatureKeyPair\":{\"wrappedSigningKey\":\"7.AA==\"},"
                + "\"securityState\":{\"securityState\":\"unsupported\"}},",
            StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var connector = new FakeKeyConnectorClient(keyConnectorKey);
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store, connector);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
        Assert.Empty(connector.Calls);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public async Task TrustedDeviceSso_DecryptsAndPersistsFreshSsoMode(
        EncryptionType envelopeType)
    {
        var expectedUserKey = Enumerable.Range(64, 64)
            .Select(value => (byte)value)
            .ToArray();
        var encryptedUserKey = RsaEnvelopePlaceholder(envelopeType);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TrustedDeviceTokenJson().Replace(
                "\"EncryptedUserKey\":\"4.AA==\"",
                $"\"EncryptedUserKey\":\"{encryptedUserKey}\"",
                StringComparison.Ordinal)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var deviceTrust = new FakeDeviceTrustService(expectedUserKey);
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            store,
            deviceTrust: deviceTrust);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(expectedUserKey, session.UserKey!.FullKey);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal(PersistedSessionUnlockMethod.TrustedDeviceSso, persisted.UnlockMethod);
        Assert.False(persisted.HasMasterPassword);
        Assert.Equal("2.AA==|AA==|AA==", deviceTrust.EncryptedPrivateKey);
        Assert.Equal(encryptedUserKey, deviceTrust.EncryptedUserKey);
        Assert.Equal(persisted.DeviceIdentifier, deviceTrust.DeviceIdentifier);
        Assert.True(deviceTrust.CapabilitySupported);
    }

    [Fact]
    public async Task TrustedDeviceSso_MalformedHistoricalMacFailsBeforeDeviceKeyAccess()
    {
        var response = TrustedDeviceTokenJson().Replace(
            "\"EncryptedUserKey\":\"4.AA==\"",
            "\"EncryptedUserKey\":\"5.AA==|AA==\"",
            StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var deviceTrust = new FakeDeviceTrustService(new byte[64]);
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            store,
            deviceTrust: deviceTrust);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
        Assert.Null(deviceTrust.DeviceIdentifier);
        Assert.False(store.TryLoad(out _));
    }

    [Fact]
    public async Task TrustedDeviceSso_V2AccountKeysWithoutMasterPasswordFailsBeforeDeviceKeyAccess()
    {
        var response = TrustedDeviceTokenJson().Replace(
            "\"PrivateKey\":null,",
            "\"PrivateKey\":null," + V2AccountKeysJson() + ",",
            StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var deviceTrust = new FakeDeviceTrustService(new byte[64]);
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            store,
            deviceTrust: deviceTrust);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.UnsupportedSsoDecryption, failure.Kind);
        Assert.Null(deviceTrust.DeviceIdentifier);
        Assert.Equal(0, deviceTrust.RecordTrustLossCount);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
    }

    [Fact]
    public async Task TrustedDeviceSso_ValidV2AccountKeysVerifyBeforeUnlock()
    {
        using var accountKeys = V2AccountKeyMaterial.Generate();
        var responseKeys = new AccountKeysResponse(
            new PublicKeyEncryptionKeyPairResponse(
                accountKeys.WrappedPrivateKey,
                accountKeys.AccountPublicKey,
                accountKeys.SignedPublicKey),
            new SignatureKeyPairResponse(
                accountKeys.WrappedSigningKey,
                accountKeys.VerifyingKey),
            new AccountSecurityState(
                accountKeys.SecurityState,
                accountKeys.SecurityVersion));
        var token = new TokenResponse(
            AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            Iterations,
            UserDecryptionOptions: new UserDecryptionOptionsResponse(
                false,
                TrustedDeviceOption: new TrustedDeviceUserDecryptionOptionResponse(
                    true,
                    true,
                    false,
                    false,
                    "2.AA==|AA==|AA==",
                    "4.AA==")),
            AccountKeys: responseKeys);
        var accountKeysJson = JsonSerializer.Serialize(
            responseKeys,
            ApiJsonContext.Default.AccountKeysResponse);
        using var accountKeysDocument = JsonDocument.Parse(accountKeysJson);
        var sync = new SyncResponse(
            "sync",
            new ProfileDto(Email, Email, "Me", null, null)
            {
                AccountKeys = accountKeysDocument.RootElement.Clone(),
                SecurityStamp = "stamp-1",
            },
            [],
            []);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(token, ApiJsonContext.Default.TokenResponse)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(sync, ApiJsonContext.Default.SyncResponse)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var transport = accountKeys.UserKey.ExportTransport();
        try
        {
            var deviceTrust = new FakeDeviceTrustService(transport);
            var store = new MemoryTokenStore();
            var session = new VaultSession();
            var service = CreateAuthService(
                handler,
                session,
                store,
                deviceTrust: deviceTrust);

            var result = await service.LoginWithSsoAsync(
                service.BeginAuthenticationAttempt(),
                "https://vault.example",
                Email,
                SsoAuthorization(),
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.Success>(result);
            Assert.True(session.UsesV2UserKey);
            Assert.Equal(2, session.SecurityVersion);
            Assert.Null(session.UserKey);
            Assert.Equal(accountKeys.WrappedPrivateKey, session.EncryptedPrivateKey);
            Assert.True(store.TryLoad(out var persisted));
            Assert.Equal(PersistedSessionUnlockMethod.TrustedDeviceSso, persisted.UnlockMethod);
            Assert.NotNull(deviceTrust.DeviceIdentifier);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
    }

    [Fact]
    public async Task TrustedDeviceSso_V2UsesValidatedCacheWhenOnlineSyncIsUnavailable()
    {
        using var accountKeys = V2AccountKeyMaterial.Generate();
        var responseKeys = new AccountKeysResponse(
            new PublicKeyEncryptionKeyPairResponse(
                accountKeys.WrappedPrivateKey,
                accountKeys.AccountPublicKey,
                accountKeys.SignedPublicKey),
            new SignatureKeyPairResponse(
                accountKeys.WrappedSigningKey,
                accountKeys.VerifyingKey),
            new AccountSecurityState(accountKeys.SecurityState, 2));
        var token = new TokenResponse(
            AccessToken(),
            "refresh",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            Iterations,
            UserDecryptionOptions: new UserDecryptionOptionsResponse(
                false,
                TrustedDeviceOption: new TrustedDeviceUserDecryptionOptionResponse(
                    true,
                    true,
                    false,
                    false,
                    "2.AA==|AA==|AA==",
                    "4.AA==")),
            AccountKeys: responseKeys);
        var keysJson = JsonSerializer.Serialize(
            responseKeys,
            ApiJsonContext.Default.AccountKeysResponse);
        using var keysDocument = JsonDocument.Parse(keysJson);
        var sync = new SyncResponse(
            "sync",
            new ProfileDto(Email, Email, "Me", null, null)
            {
                AccountKeys = keysDocument.RootElement.Clone(),
            },
            [],
            []);
        var syncJson = JsonSerializer.Serialize(sync, ApiJsonContext.Default.SyncResponse);
        var cacheStore = new FakeCacheStore();
        cacheStore.SaveVaultCache(new VaultCacheEntry(
            VaultCacheEntry.CurrentSchemaVersion,
            Email,
            ServerEndpoints.FromWebVault("https://vault.example"),
            generation: 1,
            Email,
            DateTimeOffset.UtcNow,
            syncJson));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(token, ApiJsonContext.Default.TokenResponse)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.ServiceUnavailable,
            """{"message":"offline"}"""));
        var transport = accountKeys.UserKey.ExportTransport();
        try
        {
            var store = new MemoryTokenStore();
            var session = new VaultSession();
            var service = CreateAuthService(
                handler,
                session,
                store,
                deviceTrust: new FakeDeviceTrustService(transport),
                cacheStore: cacheStore);

            var result = await service.LoginWithSsoAsync(
                service.BeginAuthenticationAttempt(),
                "https://vault.example",
                Email,
                SsoAuthorization(),
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.Success>(result);
            Assert.True(session.UsesV2UserKey);
            Assert.True(session.DataStale);
            Assert.Equal(Core.Session.VaultState.Unlocked, session.State);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrustedDeviceSso_MissingEnvelopeRecordsTrustLossBeforeApproval(
        bool missingPrivateKey)
    {
        var response = TrustedDeviceTokenJson().Replace(
            missingPrivateKey
                ? "\"EncryptedPrivateKey\":\"2.AA==|AA==|AA==\""
                : "\"EncryptedUserKey\":\"4.AA==\"",
            missingPrivateKey
                ? "\"EncryptedPrivateKey\":null"
                : "\"EncryptedUserKey\":null",
            StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var deviceTrust = new FakeDeviceTrustService(new byte[64]);
        var store = new MemoryTokenStore();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            deviceTrust: deviceTrust);

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.TrustedDeviceApprovalRequired>(result);
        Assert.Equal(1, deviceTrust.RecordTrustLossCount);
        Assert.Equal(Email, deviceTrust.TrustLossAccountId);
        Assert.Equal("https://vault.example", deviceTrust.TrustLossEndpoints!.WebVault.Value);
        Assert.NotNull(deviceTrust.TrustLossDeviceIdentifier);
        Assert.Null(deviceTrust.DeviceIdentifier);
        Assert.True(store.TryLoad(out _));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TrustedDeviceSso_ExposesEveryAvailableApprovalMethod(
        bool hasAdminApproval,
        bool hasLoginApprovingDevice)
    {
        var response = TrustedDeviceTokenJson()
            .Replace(
                "\"HasAdminApproval\":true",
                $"\"HasAdminApproval\":{hasAdminApproval.ToString().ToLowerInvariant()}",
                StringComparison.Ordinal)
            .Replace(
                "\"HasLoginApprovingDevice\":true",
                $"\"HasLoginApprovingDevice\":{hasLoginApprovingDevice.ToString().ToLowerInvariant()}",
                StringComparison.Ordinal);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            new MemoryTokenStore());

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var approval = Assert.IsType<AuthResult.TrustedDeviceApprovalRequired>(result);
        Assert.Equal(hasAdminApproval, approval.HasAdminApproval);
        Assert.Equal(hasLoginApprovingDevice, approval.HasLoginApprovingDevice);
    }

    [Fact]
    public async Task TrustedDeviceSso_UntrustedDevice_CompletesAfterOtherDeviceApproval()
    {
        var expectedUserKey = Enumerable.Range(64, 64)
            .Select(value => (byte)value)
            .ToArray();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TrustedDeviceTokenJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var store = new MemoryTokenStore();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, store);

        var authenticated = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var approval = Assert.IsType<AuthResult.TrustedDeviceApprovalRequired>(authenticated);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.True(store.TryLoad(out var pending));
        Assert.Equal(PersistedSessionUnlockMethod.TrustedDeviceSso, pending.UnlockMethod);
        Assert.False(pending.HasMasterPassword);

        var completed = await service.CompleteTrustedDeviceApprovalAsync(
            approval.Lease,
            expectedUserKey.ToArray(),
            trustCurrentDevice: false,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(completed);
        Assert.Equal(expectedUserKey, session.UserKey!.FullKey);
        Assert.Equal(Core.Session.VaultState.Unlocked, session.State);
    }

    [Fact]
    public async Task TrustedDeviceSso_SessionPersistenceFailureRollsBackAccountSession()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            TrustedDeviceTokenJson()));
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            new RejectingTokenStore(),
            deviceTrust: new FakeDeviceTrustService(new byte[64]));

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
        Assert.Null(session.UserKey);
        Assert.Null(session.AccessToken);
    }

    [Fact]
    public async Task KeyConnectorSso_SessionPersistenceFailureRollsBackAccountSession()
    {
        var masterKeyBytes = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        using var stretchedKey = _crypto.StretchMasterKey(masterKeyBytes);
        using var userKey = new SymmetricCryptoKey(new byte[64]);
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            KeyConnectorTokenJson(protectedUserKey)));
        var session = new VaultSession();
        var service = CreateAuthService(
            handler,
            session,
            new RejectingTokenStore(),
            keyConnector: new FakeKeyConnectorClient(masterKeyBytes));

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
        Assert.Null(session.UserKey);
        Assert.Null(session.AccessToken);
    }

    [Fact]
    public async Task KeyConnectorUnlock_SessionLockCancelsMasterKeyRequest()
    {
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            ServerEndpoints.FromWebVault("https://vault.example"),
            1,
            Email,
            "device-id",
            "refresh-token",
            "2.invalid",
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            unlockMethod: PersistedSessionUnlockMethod.KeyConnector,
            keyConnectorUrl: "https://keys.example"));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken()}}","refresh_token":"refreshed-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        var connector = new BlockingKeyConnectorClient();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            keyConnector: connector);

        var unlock = service.UnlockWithKeyConnectorAsync(
            service.BeginAuthenticationAttempt(),
            TestContext.Current.CancellationToken);
        await connector.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.LockAsync(TestContext.Current.CancellationToken);
        var result = await unlock;

        Assert.IsType<AuthResult.Failure>(result);
        Assert.True(connector.CancellationObserved);
    }

    [Fact]
    public async Task KeyConnectorUnlock_PendingAccountDeletionBlocksBeforeRefresh()
    {
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            ServerEndpoints.FromWebVault("https://vault.example"),
            1,
            Email,
            "device-id",
            "refresh-token",
            "2.invalid",
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            unlockMethod: PersistedSessionUnlockMethod.KeyConnector,
            keyConnectorUrl: "https://keys.example");
        var store = new MemoryTokenStore();
        store.Save(persisted);
        var handler = new FakeHttpMessageHandler();
        var connector = new FakeKeyConnectorClient(new byte[32]);
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            keyConnector: connector,
            accountDeletionTombstones:
                FixedAccountDeletionTombstoneStore.ForSession(persisted));

        var result = await service.UnlockWithKeyConnectorAsync(
            service.BeginAuthenticationAttempt(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.AccountDeletionRecoveryRequired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Empty(handler.Requests);
        Assert.Empty(connector.Calls);
    }

    [Fact]
    public async Task MasterPasswordUnlock_PendingAccountDeletionBlocksBeforeKeyDerivation()
    {
        var fixture = CreateLoginFixture();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            ServerEndpoints.FromWebVault("https://vault.example"),
            1,
            Email,
            "device-id",
            "refresh-token",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            Email);
        var store = new MemoryTokenStore();
        store.Save(persisted);
        var handler = new FakeHttpMessageHandler();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            accountDeletionTombstones:
                FixedAccountDeletionTombstoneStore.ForSession(persisted));

        var result = await service.UnlockAsync(
            Password,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.AccountDeletionRecoveryRequired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Sso_KeyConnectorAndTrustedDeviceConflict_IsRejected()
    {
        var handler = new FakeHttpMessageHandler();
        var response = TrustedDeviceTokenJson().Replace(
            "\"TrustedDeviceOption\":{",
            "\"KeyConnectorOption\":{\"KeyConnectorUrl\":\"https://keys.example\"},\"TrustedDeviceOption\":{",
            StringComparison.Ordinal);
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var store = new MemoryTokenStore();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            store,
            keyConnector: new FakeKeyConnectorClient(new byte[32]),
            deviceTrust: new FakeDeviceTrustService(new byte[64]));

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.InvalidResponse, failure.Kind);
        Assert.False(store.TryLoad(out _));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LoginWithSsoAsync_EmailTwoFactor_RetriesAuthorizationCodeAndPersistsRememberToken()
    {
        var fixture = CreateLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["1"],"SsoEmail2faSessionToken":"sso-session","Email":"me@example.com"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SsoTokenJson(fixture.ProtectedUserKey, Email, "remember-sso")));
        var store = new MemoryTokenStore();
        var service = CreateAuthService(handler, new VaultSession(), store);
        var attempt = service.BeginAuthenticationAttempt();

        var first = await service.LoginWithSsoAsync(
            attempt,
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.TwoFactorRequired>(first);
        Assert.Contains("\"email\":\"me@example.com\"", handler.Bodies[1]);
        Assert.Contains("\"ssoEmail2FaSessionToken\":\"sso-session\"", handler.Bodies[1]);

        var second = await service.SubmitTwoFactorAsync(
            attempt,
            "123456",
            (int)TwoFactorProviderType.Email,
            TestContext.Current.CancellationToken,
            rememberDevice: true);

        Assert.IsType<AuthResult.SsoAuthenticated>(second);
        var form = ParseForm(handler.Bodies[2]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("sso-code", form["code"]);
        Assert.Equal(SsoVerifier, form["code_verifier"]);
        Assert.Equal("1", form["twoFactorProvider"]);
        Assert.Equal("123456", form["twoFactorToken"]);
        Assert.Equal("1", form["twoFactorRemember"]);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal("remember-sso", persisted.TwoFactorToken);
    }

    [Fact]
    public async Task SubmitSsoSingleUseTwoFactorAsync_UncertainFailureExpiresPendingContext()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["7"]}"""));
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("SENSITIVE-ENDPOINT-DETAIL")));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, new MemoryTokenStore());
        var attempt = service.BeginAuthenticationAttempt();

        Assert.IsType<AuthResult.TwoFactorRequired>(await service.LoginWithSsoAsync(
            attempt,
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken));

        var failed = await service.SubmitTwoFactorAsync(
            attempt,
            "single-use-assertion",
            (int)TwoFactorProviderType.WebAuthn,
            TestContext.Current.CancellationToken,
            rememberDevice: false);

        var failure = Assert.IsType<AuthResult.Failure>(failed);
        Assert.Equal(AuthFailureKind.SessionExpired, failure.Kind);
        Assert.False(session.IsCurrent(attempt));
        Assert.IsType<AuthResult.Failure>(await service.SubmitTwoFactorAsync(
            attempt,
            "same-assertion",
            (int)TwoFactorProviderType.WebAuthn,
            TestContext.Current.CancellationToken,
            rememberDevice: false));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task LoginWithSsoAsync_VaultwardenEmailTwoFactor_SendsDeviceOnly()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["1"]}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var service = CreateAuthService(handler, new VaultSession(), new MemoryTokenStore());

        var result = await service.LoginWithSsoAsync(
            service.BeginAuthenticationAttempt(),
            "https://vault.example",
            Email,
            SsoAuthorization(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.TwoFactorRequired>(result);
        Assert.Contains("\"email\":null", handler.Bodies[1]);
        Assert.Contains("\"masterPasswordHash\":null", handler.Bodies[1]);
        Assert.Contains("\"deviceIdentifier\":", handler.Bodies[1]);
        Assert.Contains("\"ssoEmail2FaSessionToken\":null", handler.Bodies[1]);
    }

    [Fact]
    public async Task LogoutAsync_RemovesOnlyCurrentAccountAndKeepsAnotherSavedSession()
    {
        var store = new MemoryTokenStore();
        var remaining = SavedSession("account-a", "https://a.example");
        var active = SavedSession("account-b", "https://b.example");
        store.Save(remaining);
        store.Save(active);
        var session = new VaultSession();
        session.RestorePersistedSession(active);
        var identity = new ControlledIdentityApiClient(_ =>
            Task.FromException<ConnectTokenResult>(new InvalidOperationException("Not expected.")));
        var service = CreateAuthService(identity, session, store);

        await service.LogoutAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
        Assert.True(store.TryLoad(out var next));
        Assert.Equal(remaining.AccountId, next.AccountId);
        Assert.Single(store.GetSavedAccounts());
    }

    [Fact]
    public async Task ConditionalLogout_RemovesPinCredentialSoOfflinePinCannotBeReactivated()
    {
        var store = new MemoryTokenStore();
        var active = SavedSession("account-pin", "https://pin.example") with
        {
            PinProtectedUserKey = "2.pin-protected-key",
            PinFailedAttempts = 2,
        };
        store.Save(active);
        var session = new VaultSession();
        var expected = session.RestorePersistedSession(active);
        var identity = new ControlledIdentityApiClient(_ =>
            Task.FromException<ConnectTokenResult>(new InvalidOperationException("Not expected.")));
        var service = CreateAuthService(identity, session, store);

        var claimed = await service.LogoutAsync(
            expected,
            TestContext.Current.CancellationToken);

        Assert.True(claimed);
        Assert.Equal(Core.Session.VaultState.LoggedOut, session.State);
        Assert.Empty(store.GetSavedAccounts());
        Assert.False(store.TryActivate(active.AccountId, active.Endpoints!, out _));
    }

    private AuthService CreateAuthService(
        FakeHttpMessageHandler handler,
        VaultSession session,
        ITokenStore tokenStore,
        IKeyConnectorClient? keyConnector = null,
        IDeviceTrustService? deviceTrust = null,
        IDeviceIdentifierProvider? deviceIdentifierProvider = null,
        ICacheStore? cacheStore = null,
        TimeProvider? timeProvider = null,
        TimeSpan? newDeviceVerificationLifetime = null,
        CryptoService? crypto = null,
        IAccountDeletionTombstoneStore? accountDeletionTombstones = null,
        IAccountDeletionCoordinationGate? accountDeletionCoordinationGate = null)
    {
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));
        var effectiveCrypto = crypto ?? _crypto;
        var decryptor = new VaultDecryptor(effectiveCrypto);
        var cache = new VaultCacheService(cacheStore ?? new FakeCacheStore(), decryptor, session);
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IIdentityApiClient>(api)
            .AddAnonymous<IServerConfigApiClient>(api)
            .AddAuthenticated<IReadonlyApiClient>(api)
            .AddAuthenticated<IAccountApiClient>(api);
        var bootstrapper = new VaultBootstrapper(new SyncService(provider, decryptor, session, cache), provider, session);
        var refresher = new TokenRefreshService(
            provider,
            session,
            tokenStore,
            accountDeletionCoordinationGate: accountDeletionCoordinationGate);
        return new AuthService(
            provider,
            effectiveCrypto,
            session,
            tokenStore,
            bootstrapper,
            cache,
            refresher,
            keyConnector,
            deviceTrust,
            deviceIdentifierProvider,
            timeProvider: timeProvider,
            newDeviceVerificationLifetime: newDeviceVerificationLifetime,
            accountDeletionTombstones: accountDeletionTombstones,
            accountDeletionCoordinationGate: accountDeletionCoordinationGate);
    }

    private AuthService CreateAuthService(
        IIdentityApiClient identity,
        VaultSession session,
        ITokenStore tokenStore,
        IKeyConnectorClient? keyConnector = null,
        IDeviceTrustService? deviceTrust = null,
        IDeviceIdentifierProvider? deviceIdentifierProvider = null)
    {
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var provider = FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        return new AuthService(
            provider,
            _crypto,
            session,
            tokenStore,
            bootstrapper: null!,
            cache,
            refresher,
            keyConnector,
            deviceTrust,
            deviceIdentifierProvider);
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
        var payload = Base64Url($$"""{"sub":"{{accountId}}","email":"{{Email}}"}""");
        return $"{header}.{payload}.signature";
    }

    private static SsoAuthorizationCode SsoAuthorization() => new(
        "sso-code",
        SsoVerifier,
        "http://localhost:8065",
        SsoVerifier,
        "organization");

    private static string SsoTokenJson(
        string protectedUserKey,
        string salt,
        string? twoFactorToken = null)
    {
        var remember = twoFactorToken is null
            ? string.Empty
            : $",\"TwoFactorToken\":\"{twoFactorToken}\"";
        return $$$$"""
        {"access_token":"{{{{AccessToken()}}}}","refresh_token":"refresh","expires_in":3600,
         "token_type":"Bearer","scope":"api offline_access","Key":"{{{{protectedUserKey}}}}",
         "PrivateKey":null,"Kdf":0,"KdfIterations":{{{{Iterations}}}}{{{{remember}}}},
         "UserDecryptionOptions":{"HasMasterPassword":true,"MasterPasswordUnlock":{
           "Kdf":{"KdfType":0,"Iterations":{{{{Iterations}}}}},
           "MasterKeyWrappedUserKey":"{{{{protectedUserKey}}}}","Salt":"{{{{salt}}}}"}}}
        """;
    }

    private static string KeyConnectorTokenJson(string protectedUserKey) =>
        $$$$"""
        {"access_token":"{{{{AccessToken()}}}}","refresh_token":"refresh","expires_in":3600,
         "token_type":"Bearer","scope":"api offline_access","Key":"{{{{protectedUserKey}}}}",
         "PrivateKey":null,"Kdf":0,"KdfIterations":600000,
         "UserDecryptionOptions":{"HasMasterPassword":false,
           "KeyConnectorOption":{"KeyConnectorUrl":"https://keys.example/base"}}}
        """;

    private static string TrustedDeviceTokenJson() =>
        $$$$"""
        {"access_token":"{{{{AccessToken()}}}}","refresh_token":"refresh","expires_in":3600,
         "token_type":"Bearer","scope":"api offline_access","PrivateKey":null,
         "Kdf":0,"KdfIterations":600000,
         "UserDecryptionOptions":{"HasMasterPassword":false,
           "TrustedDeviceOption":{"HasAdminApproval":true,"HasLoginApprovingDevice":true,
             "HasManageResetPasswordPermission":false,"IsTdeOffboarding":false,
             "EncryptedPrivateKey":"2.AA==|AA==|AA==","EncryptedUserKey":"4.AA=="}}}
        """;

    private static string RsaEnvelopePlaceholder(EncryptionType type)
    {
        var data = Convert.ToBase64String([0]);
        return type.HasLegacyRsaMac()
            ? $"{(int)type}.{data}|{Convert.ToBase64String(new byte[EncString.LegacyRsaMacLength])}"
            : $"{(int)type}.{data}";
    }

    private static string V2AccountKeysJson() =>
        "\"AccountKeys\":{"
            + "\"publicKeyEncryptionKeyPair\":{\"wrappedPrivateKey\":\"7.AA==\"},"
            + "\"signatureKeyPair\":{\"wrappedSigningKey\":\"7.AA==\"},"
            + "\"securityState\":{\"securityState\":\"unsupported\"}}";

    private static string TokenJson(string protectedUserKey) =>
        $$"""{"access_token":"{{AccessToken()}}","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"{{protectedUserKey}}","PrivateKey":null,"Kdf":0,"KdfIterations":{{Iterations}}}""";

    private static string RequiredPasswordTokenJson(
        string protectedUserKey,
        bool forcePasswordReset,
        int minLength,
        bool enforceOnLogin,
        bool requireSpecial = false) =>
        JsonSerializer.Serialize(new
        {
            access_token = AccessToken(),
            refresh_token = "refresh",
            expires_in = 3600,
            token_type = "Bearer",
            scope = "api offline_access",
            Key = protectedUserKey,
            PrivateKey = (string?)null,
            Kdf = 0,
            KdfIterations = Iterations,
            ForcePasswordReset = forcePasswordReset,
            MasterPasswordPolicy = new
            {
                Object = "masterPasswordPolicy",
                MinComplexity = (int?)null,
                MinLength = minLength,
                RequireUpper = false,
                RequireLower = false,
                RequireNumbers = false,
                RequireSpecial = requireSpecial,
                EnforceOnLogin = enforceOnLogin,
            },
        });

    private static string TokenJsonWithTwoFactorToken(string protectedUserKey, string twoFactorToken) =>
        $$"""{"access_token":"{{AccessToken()}}","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"{{protectedUserKey}}","PrivateKey":null,"Kdf":0,"KdfIterations":{{Iterations}},"TwoFactorToken":"{{twoFactorToken}}"}""";

    private static string SyncJson() =>
        """{"object":"sync","profile":{"id":"me@example.com","email":"me@example.com","name":"Me","securityStamp":"stamp-1"},"folders":[],"ciphers":[]}""";

    private static string DevicesJson() =>
        """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}""";

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    private static PersistedSession SavedSession(string accountId, string serverUrl) => new(
        PersistedSession.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        $"device-{accountId}",
        $"refresh-{accountId}",
        $"key-{accountId}",
        KdfType.Pbkdf2,
        Iterations,
        null,
        null);

    private sealed class FakeKeyConnectorClient(byte[] masterKey) : IKeyConnectorClient
    {
        public List<(string Url, string AccessToken)> Calls { get; } = [];

        public Task<byte[]> GetMasterKeyAsync(
            string keyConnectorUrl,
            string accessToken,
            CancellationToken ct = default)
        {
            Calls.Add((keyConnectorUrl, accessToken));
            return Task.FromResult(masterKey.ToArray());
        }
    }

    private sealed class BlockingKeyConnectorClient : IKeyConnectorClient
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async Task<byte[]> GetMasterKeyAsync(
            string keyConnectorUrl,
            string accessToken,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("The blocking request completed unexpectedly.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class FakeDeviceTrustService(byte[] userKey) : IDeviceTrustService
    {
        public bool CanTrustCurrentDevice => false;
        public bool IsCurrentDeviceTrusted => true;
        public string? DeviceIdentifier { get; private set; }
        public string? EncryptedPrivateKey { get; private set; }
        public string? EncryptedUserKey { get; private set; }
        public bool CapabilitySupported { get; private set; }
        public int CapabilitySetCount { get; private set; }
        public int RecordTrustLossCount { get; private set; }
        public string? TrustLossAccountId { get; private set; }
        public ServerEndpoints? TrustLossEndpoints { get; private set; }
        public string? TrustLossDeviceIdentifier { get; private set; }

        public void SetCurrentSessionCapability(
            Core.Session.VaultSessionLease lease,
            bool supported)
        {
            CapabilitySetCount++;
            CapabilitySupported = supported;
        }

        public void RecordTrustLoss(
            string accountId,
            ServerEndpoints endpoints,
            string deviceIdentifier)
        {
            RecordTrustLossCount++;
            TrustLossAccountId = accountId;
            TrustLossEndpoints = endpoints;
            TrustLossDeviceIdentifier = deviceIdentifier;
        }

        public bool TryDecryptUserKey(
            string accountId,
            ServerEndpoints endpoints,
            string deviceIdentifier,
            string encryptedPrivateKey,
            string encryptedUserKey,
            out byte[] decryptedUserKey)
        {
            DeviceIdentifier = deviceIdentifier;
            EncryptedPrivateKey = encryptedPrivateKey;
            EncryptedUserKey = encryptedUserKey;
            decryptedUserKey = userKey.ToArray();
            return true;
        }

        public Task TrustCurrentDeviceAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FixedDeviceIdentifierProvider(string identifier)
        : IDeviceIdentifierProvider
    {
        public string GetDeviceIdentifier() => identifier;
    }

    private sealed class RejectingTokenStore : ITokenStore
    {
        public bool TryLoad(out PersistedSession session)
        {
            session = null!;
            return false;
        }

        public void Save(PersistedSession session) =>
            throw new IOException("simulated persistence failure");

        public void Clear()
        {
        }
    }

    private sealed class FailingRemovalTokenStore(PersistedSession persisted) : ITokenStore
    {
        public bool RemoveCalled { get; private set; }

        public bool TryLoad(out PersistedSession session)
        {
            session = persisted;
            return true;
        }

        public void Save(PersistedSession session)
        {
        }

        public void Clear()
        {
        }

        public bool Remove(string accountId, ServerEndpoints endpoints)
        {
            RemoveCalled = true;
            return false;
        }
    }

    private sealed class BlockingDecryptCryptoService : CryptoService
    {
        private readonly ManualResetEventSlim _release = new(false);

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override SymmetricCryptoKey DecryptUserKey(
            SymmetricCryptoKey stretchedKey,
            EncString protectedUserKey)
        {
            Entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The forced-password test barrier was not released.");
            return base.DecryptUserKey(stretchedKey, protectedUserKey);
        }

        public void Release() => _release.Set();
    }

    private sealed record LoginFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey, string PasswordHash);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
