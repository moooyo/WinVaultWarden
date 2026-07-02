using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class AuthServicePinTests
{
    private const string Email = "me@example.com";
    private const string Pin = "1234";
    private const int Iterations = 600_000;

    private readonly CryptoService _crypto = new();

    [Fact]
    public async Task UnlockWithPinAsync_CorrectPin_RefreshesTokenAndResetsFailedAttempts()
    {
        var fixture = CreatePinFixture();
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
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
            PinFailedAttempts = 3,
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync(Pin, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal("new-access", session.AccessToken);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal("new-refresh", persisted.RefreshToken);
        Assert.Equal(0, persisted.PinFailedAttempts);
        Assert.Equal(fixture.PinProtectedUserKey, persisted.PinProtectedUserKey);
    }

    [Fact]
    public async Task UnlockWithPinAsync_WrongPin_ReturnsFailureIncrementsAttemptsAndStaysLocked()
    {
        var fixture = CreatePinFixture();
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
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
            PinFailedAttempts = 0,
        });
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync("9999", TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Contains("4", failure.Message);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Empty(handler.Requests);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(1, persisted.PinFailedAttempts);
        Assert.Equal(fixture.PinProtectedUserKey, persisted.PinProtectedUserKey);
    }

    [Fact]
    public async Task UnlockWithPinAsync_FifthConsecutiveWrongPin_ClearsPin()
    {
        var fixture = CreatePinFixture();
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
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
            PinFailedAttempts = 4,
        });
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync("9999", TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.PinCleared>(result);
        Assert.Empty(handler.Requests);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Null(persisted.PinProtectedUserKey);
        Assert.Equal(0, persisted.PinFailedAttempts);
    }

    [Fact]
    public async Task UnlockWithPinAsync_NoPinSet_ReturnsFailure()
    {
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            "https://vault.example",
            Email,
            "device-id",
            "stored-refresh",
            "2.unrelated",
            KdfType.Pbkdf2,
            Iterations,
            null,
            null));
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync(Pin, TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal("未设置 PIN。", failure.Message);
        Assert.Empty(handler.Requests);
    }

    private AuthService CreateAuthService(FakeHttpMessageHandler handler, VaultSession session, MemoryTokenStore tokenStore)
    {
        var api = new ApiClient(new HttpClient(handler));
        var decryptor = new VaultDecryptor(_crypto);
        var bootstrapper = new VaultBootstrapper(api, decryptor, session);
        return new AuthService(api, _crypto, session, tokenStore, bootstrapper);
    }

    private PinFixture CreatePinFixture()
    {
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var masterKey = _crypto.DeriveMasterKey("correct horse battery staple", Email, KdfType.Pbkdf2, Iterations, null, null);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();

        var pinMasterKey = _crypto.DeriveMasterKey(Pin, Email, KdfType.Pbkdf2, Iterations, null, null);
        var pinProtectedUserKey = _crypto.ProtectUserKey(pinMasterKey, userKey).ToString();

        return new PinFixture(userKey, protectedUserKey, pinProtectedUserKey);
    }

    private static string SyncJson() =>
        """{"object":"sync","profile":{"id":"u1","email":"me@example.com","name":"Me"},"folders":[],"ciphers":[]}""";

    private static string DevicesJson() =>
        """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}""";

    private sealed record PinFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey, string PinProtectedUserKey);
}
