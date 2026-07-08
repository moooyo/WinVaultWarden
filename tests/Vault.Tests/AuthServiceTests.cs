using System.Net;
using System.Text;
using Api;
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
        Assert.Equal("access", session.AccessToken);
        Assert.Equal("refresh", session.RefreshToken);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal("me@example.com", session.Account.Email);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal("refresh", persisted.RefreshToken);
        Assert.Equal(fixture.ProtectedUserKey, persisted.ProtectedUserKey);

        var form = ParseForm(handler.Bodies[1]);
        Assert.Equal("password", form["grant_type"]);
        Assert.Equal("desktop", form["client_id"]);
        Assert.Equal("6", form["device_type"]);
        Assert.Equal(Email, form["username"]);
        Assert.Equal(fixture.PasswordHash, form["password"]);
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
    public async Task LoginAsync_Argon2idSuccess_SavesSessionAndBootstrapsVault()
    {
        var fixture = CreateArgon2idLoginFixture();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"kdf":1,"kdfIterations":3,"kdfMemory":64,"kdfParallelism":4}"""));
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

        var second = await service.SubmitTwoFactorAsync("123456", TestContext.Current.CancellationToken, rememberDevice: false);

        Assert.IsType<AuthResult.Success>(second);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        var form = ParseForm(handler.Bodies[2]);
        Assert.Equal("0", form["two_factor_provider"]);
        Assert.Equal("123456", form["two_factor_token"]);
        Assert.Equal("0", form["two_factor_remember"]);
        Assert.Equal(fixture.PasswordHash, form["password"]);
    }

    [Fact]
    public async Task UnlockAsync_WithPersistedSession_RefreshesTokenAndBootstraps()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            "https://vault.example",
            Email,
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
    public async Task UnlockAsync_WithWrongPassword_ReturnsFailureAndStaysLocked()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession("https://vault.example", Email, "device-id", "refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockAsync("wrong password", TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
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

    private AuthService CreateAuthService(FakeHttpMessageHandler handler, VaultSession session, MemoryTokenStore tokenStore)
    {
        var api = new ApiClient(new HttpClient(handler));
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var bootstrapper = new VaultBootstrapper(new SyncService(api, decryptor, session, cache), api, session);
        return new AuthService(api, api, _crypto, session, tokenStore, bootstrapper, cache);
    }

    private LoginFixture CreateLoginFixture()
    {
        var masterKey = _crypto.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);
        var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, Password);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        return new LoginFixture(userKey, protectedUserKey, passwordHash);
    }

    private LoginFixture CreateArgon2idLoginFixture()
    {
        var masterKey = _crypto.DeriveMasterKey(Password, Email, KdfType.Argon2id, 3, 64, 4);
        var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, Password);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        return new LoginFixture(userKey, protectedUserKey, passwordHash);
    }

    private static string PreloginJson() =>
        $$"""{"kdf":0,"kdfIterations":{{Iterations}},"kdfMemory":null,"kdfParallelism":null}""";

    private static string TokenJson(string protectedUserKey) =>
        $$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":"{{protectedUserKey}}","PrivateKey":null,"Kdf":0,"KdfIterations":{{Iterations}}}""";

    private static string SyncJson() =>
        """{"object":"sync","profile":{"id":"u1","email":"me@example.com","name":"Me"},"folders":[],"ciphers":[]}""";

    private static string DevicesJson() =>
        """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}""";

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    private sealed record LoginFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey, string PasswordHash);
}
