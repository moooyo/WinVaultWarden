using System.Net;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// I-1 修复验证：SyncService 账户错配 InvalidDataException 必须被三条解锁路径视为预期失败。
// 真实 Vaultwarden 返回 profile.id = UUID，过渡期 lease.AccountId = email，两者不符时不得崩溃。
public class AuthServiceAccountMismatchTests
{
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const string Pin = "1234";
    private const string Server = "https://vault.example";
    private const int Iterations = 600_000;
    // 真实 Vaultwarden profile.id 为 UUID，不匹配过渡期 lease.AccountId = email。
    private const string MismatchedProfileId = "00000000-0000-0000-0000-000000000001";

    private readonly CryptoService _crypto = new();

    // 主密码解锁：无缓存，profile.id != AccountId → 返回 Failure + session Locked，不抛异常。
    [Fact]
    public async Task UnlockAsync_ProfileMismatch_NoCacheReturnsFailureAndLocks()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        // token 刷新成功
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        // 同步返回 profile.id = UUID（不匹配 lease.AccountId = email）
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MismatchedSyncJson()));
        var session = new VaultSession();
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
    }

    // PIN 解锁：无缓存，profile.id != AccountId → 返回 Failure + session Locked，不抛异常。
    [Fact]
    public async Task UnlockWithPinAsync_ProfileMismatch_NoCacheReturnsFailureAndLocks()
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MismatchedSyncJson()));
        var session = new VaultSession();
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockWithPinAsync(Pin, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
    }

    // Hello 解锁：无缓存，profile.id != AccountId → 返回 Failure + session Locked，不抛异常。
    [Fact]
    public async Task UnlockWithHelloAsync_ProfileMismatch_NoCacheReturnsFailureAndLocks()
    {
        var fixture = CreateLoginFixture();
        var signature = Enumerable.Repeat((byte)5, 256).ToArray();
        using var helloKey = _crypto.DeriveKeyFromSecret(signature);
        var helloProtected = _crypto.Encrypt(fixture.UserKey.FullKey, helloKey).ToString();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null)
        {
            HelloProtectedUserKey = helloProtected,
            HelloChallenge = Convert.ToBase64String(new byte[32]),
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MismatchedSyncJson()));
        var session = new VaultSession();
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockWithHelloAsync(Enumerable.Repeat((byte)5, 256).ToArray(), TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
    }

    // 主密码解锁：有匹配缓存，profile.id != AccountId → SyncService 抛错但 FinishUnlockOffline(fromCache=true)
    // → 离线解锁成功（DataStale），而不是崩溃。
    [Fact]
    public async Task UnlockAsync_ProfileMismatch_WithCacheReturnsOfflineSuccess()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, MismatchedSyncJson()));
        var session = new VaultSession();
        var store = new FakeCacheStore();
        var cache = PrePopulateCache(store, session, fixture.UserKey, "Cached");
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(Core.Session.VaultState.Unlocked, session.State);
        Assert.True(session.DataStale);
        var cipher = Assert.Single(session.Ciphers);
        Assert.Equal("Cached", cipher.Name);
    }

    private AuthService CreateAuthService(
        FakeHttpMessageHandler handler, VaultSession session, MemoryTokenStore tokenStore, VaultCacheService cache)
    {
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var decryptor = new VaultDecryptor(_crypto);
        var provider = new FakeVaultApiClientProvider().AddAnonymous<IIdentityApiClient>(api).AddAuthenticated<IReadonlyApiClient>(api);
        var bootstrapper = new VaultBootstrapper(new SyncService(provider, decryptor, session, cache), provider, session);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        return new AuthService(provider, _crypto, session, tokenStore, bootstrapper, cache, refresher);
    }

    // 缓存内 profile.id = email（匹配 lease.AccountId），TryLoadInto 能成功，fromCache = true。
    private VaultCacheService PrePopulateCache(
        FakeCacheStore store, VaultSession session, SymmetricCryptoKey userKey, string cipherName)
    {
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(store, decryptor, session);
        var json = JsonSerializer.Serialize(BuildCachedSync(cipherName, userKey), ApiJsonContext.Default.SyncResponse);
        store.SaveVaultCache(new VaultCacheEntry(
            VaultCacheService.SchemaVersion, Email, new ServerOrigin(Server), 1, Email, DateTimeOffset.UnixEpoch, json));
        return cache;
    }

    private SyncResponse BuildCachedSync(string cipherName, SymmetricCryptoKey userKey)
    {
        var name = _crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes(cipherName), userKey).ToString();
        var dto = new CipherDto(
            Id: "c1", Type: 1, Name: name, Notes: null, Key: null, OrganizationId: null, FolderId: null,
            Favorite: false, Reprompt: 0, Login: null, Card: null, Identity: null, SecureNote: null,
            SshKey: null, Fields: null, CreationDate: null, RevisionDate: null, DeletedDate: null,
            Attachments: null, PasswordHistory: null);
        return new SyncResponse("sync", new ProfileDto(Email, Email, "Me", null, null), [], [dto]);
    }

    private LoginFixture CreateLoginFixture()
    {
        var masterKey = _crypto.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        return new LoginFixture(userKey, protectedUserKey);
    }

    private PinFixture CreatePinFixture()
    {
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var masterKey = _crypto.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();
        var pinMasterKey = _crypto.DeriveMasterKey(Pin, Email, KdfType.Pbkdf2, Iterations, null, null);
        var pinProtectedUserKey = _crypto.ProtectUserKey(pinMasterKey, userKey).ToString();
        return new PinFixture(userKey, protectedUserKey, pinProtectedUserKey);
    }

    // profile.id 为 UUID，触发 SyncService 的账户错配门禁（InvalidDataException）。
    private static string MismatchedSyncJson() =>
        $$"""{"object":"sync","profile":{"id":"{{MismatchedProfileId}}","email":"{{Email}}","name":"Me"},"folders":[],"ciphers":[]}""";

    private sealed record LoginFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey);
    private sealed record PinFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey, string PinProtectedUserKey);
}
