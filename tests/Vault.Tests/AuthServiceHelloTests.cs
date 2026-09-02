using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// AuthService.UnlockWithHelloAsync:镜像 UnlockWithPinAsync 的离线缓存/刷新收尾,
// 但解包密钥来自 Windows Hello 签名派生(DeriveKeyFromSecret),而非主密码/PIN 拉伸。
public class AuthServiceHelloTests
{
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const string Server = "https://vault.example";
    private const int Iterations = 600_000;

    private readonly CryptoService _crypto = new();

    // 正确签名 → 解锁成功且从缓存出数据。
    [Fact]
    public async Task UnlockWithHelloAsync_CorrectSignature_UnlocksFromCache()
    {
        var fixture = CreateLoginFixture();
        var signature = Enumerable.Repeat((byte)5, 256).ToArray();
        using var helloKey = _crypto.DeriveKeyFromSecret(signature);
        var helloProtected = _crypto.Encrypt(fixture.UserKey.FullKey, helloKey).ToString();

        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin(Server), 1, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null)
        {
            HelloProtectedUserKey = helloProtected,
            HelloChallenge = Convert.ToBase64String(new byte[32]),
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("offline")); // 走离线缓存分支
        var session = new VaultSession();
        var store = new FakeCacheStore();
        var cache = PrePopulateCache(store, session, fixture.UserKey, "Cached");
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockWithHelloAsync(Enumerable.Repeat((byte)5, 256).ToArray(), TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.NotNull(session.UserKey);
    }

    // 错误签名 → 失败且清除 Hello(下次不再提供 Hello 解锁)。
    [Fact]
    public async Task UnlockWithHelloAsync_WrongSignature_FailsAndClearsHello()
    {
        var fixture = CreateLoginFixture();
        using var goodKey = _crypto.DeriveKeyFromSecret(Enumerable.Repeat((byte)5, 256).ToArray());
        var helloProtected = _crypto.Encrypt(fixture.UserKey.FullKey, goodKey).ToString();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin(Server), 1, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null)
        {
            HelloProtectedUserKey = helloProtected,
            HelloChallenge = Convert.ToBase64String(new byte[32]),
        });
        var session = new VaultSession();
        var service = CreateAuthService(new FakeHttpMessageHandler(), session, tokenStore,
            new VaultCacheService(new FakeCacheStore(), new VaultDecryptor(_crypto), session));

        var result = await service.UnlockWithHelloAsync(Enumerable.Repeat((byte)9, 256).ToArray(), TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.True(tokenStore.TryLoad(out var p));
        Assert.Null(p.HelloProtectedUserKey); // 已清除
        Assert.Null(p.HelloChallenge); // 挑战一并清除
    }

    // 未启用 Hello → 失败。
    [Fact]
    public async Task UnlockWithHelloAsync_NotEnabled_Fails()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin(Server), 1, Email, "device-id", "r",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var session = new VaultSession();
        var service = CreateAuthService(new FakeHttpMessageHandler(), session, tokenStore,
            new VaultCacheService(new FakeCacheStore(), new VaultDecryptor(new CryptoService()), session));

        var result = await service.UnlockWithHelloAsync(new byte[256], TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.Failure>(result);
    }

    [Fact]
    public async Task UnlockWithHelloAsync_PendingAccountDeletionBlocksAndClearsSignature()
    {
        var fixture = CreateLoginFixture();
        var signature = Enumerable.Repeat((byte)5, 256).ToArray();
        using var helloKey = _crypto.DeriveKeyFromSecret(signature);
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin(Server),
            1,
            Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            HelloProtectedUserKey = _crypto.Encrypt(
                fixture.UserKey.FullKey,
                helloKey).ToString(),
        };
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(persisted);
        var session = new VaultSession();
        var service = CreateAuthService(
            new FakeHttpMessageHandler(),
            session,
            tokenStore,
            new VaultCacheService(
                new FakeCacheStore(),
                new VaultDecryptor(_crypto),
                session),
            FixedAccountDeletionTombstoneStore.ForSession(persisted));

        var result = await service.UnlockWithHelloAsync(
            signature,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.AccountDeletionRecoveryRequired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.All(signature, value => Assert.Equal(0, value));
        Assert.Null(session.UserKey);
    }

    private AuthService CreateAuthService(
        FakeHttpMessageHandler handler,
        VaultSession session,
        MemoryTokenStore tokenStore,
        VaultCacheService cache,
        IAccountDeletionTombstoneStore? accountDeletionTombstones = null)
    {
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));
        var decryptor = new VaultDecryptor(_crypto);
        var provider = new FakeVaultApiClientProvider().AddAnonymous<IIdentityApiClient>(api).AddAuthenticated<IReadonlyApiClient>(api);
        var bootstrapper = new VaultBootstrapper(new SyncService(provider, decryptor, session, cache), provider, session);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        return new AuthService(
            provider,
            _crypto,
            session,
            tokenStore,
            bootstrapper,
            cache,
            refresher,
            accountDeletionTombstones: accountDeletionTombstones);
    }

    // 预置一条加密缓存(直接写 store,不经过 session 解锁/加锁,避免 Dispose 清零测试仍要复用的 fixture.UserKey)。
    private VaultCacheService PrePopulateCache(
        FakeCacheStore store, VaultSession session, SymmetricCryptoKey userKey, string cipherName)
    {
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(store, decryptor, session);
        var json = JsonSerializer.Serialize(BuildSync(cipherName, userKey), ApiJsonContext.Default.SyncResponse);
        store.SaveVaultCache(new VaultCacheEntry(
            VaultCacheService.SchemaVersion, Email, new ServerOrigin(Server), 1, Email, DateTimeOffset.UnixEpoch, json));
        return cache;
    }

    private SyncResponse BuildSync(string cipherName, SymmetricCryptoKey userKey)
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

    private sealed record LoginFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey);
}
