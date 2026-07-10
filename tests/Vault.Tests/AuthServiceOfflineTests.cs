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

// AuthService 离线解锁分支:本地已用主密码解出 UserKey 后,先尝试从加密缓存立即出数据,
// 再联网刷新 token/整库同步;网络失败时按"有无缓存"分流(有缓存→保持离线解锁成功,无缓存→回滚失败)。
public class AuthServiceOfflineTests
{
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const string Server = "https://vault.example";
    private const int Iterations = 600_000;

    private readonly CryptoService _crypto = new();

    [Fact]
    public async Task UnlockAsync_WithCacheAndNetworkFailure_SucceedsOfflineFromCache()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("offline"));
        var session = new VaultSession();
        var store = new FakeCacheStore();
        var cache = PrePopulateCache(store, session, fixture.UserKey, "Cached");
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        var cipher = Assert.Single(session.Ciphers);
        Assert.Equal("Cached", cipher.Name);
        Assert.True(session.DataStale);
        Assert.Equal(Core.Session.VaultState.Unlocked, session.State);
        Assert.NotNull(session.UserKey);
    }

    [Fact]
    public async Task UnlockAsync_WithCacheAndNetworkSuccess_RefreshesFromServerAndUpdatesCache()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, FreshSyncJson(fixture.UserKey)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var store = new FakeCacheStore();
        var cache = PrePopulateCache(store, session, fixture.UserKey, "Cached");
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.False(session.DataStale);
        var cipher = Assert.Single(session.Ciphers);
        Assert.Equal("Fresh", cipher.Name);
        Assert.Equal("new-access", session.AccessToken);
        Assert.Equal(2, store.SaveCount); // 预置 1 次 + bootstrap 内 SyncCoreAsync 成功后再写 1 次
    }

    [Fact]
    public async Task UnlockAsync_NoCacheAndNetworkFailure_FailsAndStaysLocked()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("offline"));
        var session = new VaultSession();
        var store = new FakeCacheStore(); // 空缓存,从未 Save 过
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(store, decryptor, session);
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Null(session.UserKey);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
    }

    // B2:离线解锁成功后,必须把持久化 refresh token 种回内存,否则联网后 401 无法触发刷新。
    [Fact]
    public async Task UnlockAsync_OfflineFromCache_SeedsRefreshTokenForLaterRecovery()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("offline")); // 刷新失败 → 走离线缓存分支
        var session = new VaultSession();
        var store = new FakeCacheStore();
        var cache = PrePopulateCache(store, session, fixture.UserKey, "Cached");
        var service = CreateAuthService(handler, session, tokenStore, cache);

        var result = await service.UnlockAsync(Password, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal("stored-refresh", session.RefreshToken); // B2:refresh token 已回种
        Assert.Null(session.AccessToken);                     // 离线仍无 access token
    }

    // B2:回种的 refresh token 让联网后的 TryRefreshAsync 能真实刷新、透明转在线。
    [Fact]
    public async Task UnlockAsync_OfflineThenNetworkRestored_TokenRefreshRecoversOnline()
    {
        var fixture = CreateLoginFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(Server, Email, "device-id", "stored-refresh",
            fixture.ProtectedUserKey, KdfType.Pbkdf2, Iterations, null, null));

        // 1) 离线解锁:刷新失败 → 从缓存出数据 + 回种 refresh token
        var offlineHandler = new FakeHttpMessageHandler();
        offlineHandler.Enqueue(_ => throw new HttpRequestException("offline"));
        var session = new VaultSession();
        var store = new FakeCacheStore();
        var cache = PrePopulateCache(store, session, fixture.UserKey, "Cached");
        var service = CreateAuthService(offlineHandler, session, tokenStore, cache);
        Assert.IsType<AuthResult.Success>(await service.UnlockAsync(Password, TestContext.Current.CancellationToken));
        Assert.True(session.DataStale);

        // 2) 网络恢复:凭回种的 refresh token 触发真实刷新
        var onlineHandler = new FakeHttpMessageHandler();
        onlineHandler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"recovered-access","refresh_token":"rotated-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        var onlineApi = new ApiClient(new HttpClient(onlineHandler), new ServerOrigin("https://vault.example"));
        var refresher = new TokenRefreshService(FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(onlineApi), session, tokenStore);

        var refreshed = await refresher.TryRefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(refreshed);
        Assert.Equal("recovered-access", session.AccessToken);
        Assert.Equal("rotated-refresh", session.RefreshToken);
    }

    private AuthService CreateAuthService(
        FakeHttpMessageHandler handler, VaultSession session, MemoryTokenStore tokenStore, VaultCacheService cache)
    {
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));
        var decryptor = new VaultDecryptor(_crypto);
        var provider = new FakeVaultApiClientProvider().AddAnonymous<IIdentityApiClient>(api).AddAuthenticated<IReadonlyApiClient>(api);
        var bootstrapper = new VaultBootstrapper(new SyncService(provider, decryptor, session, cache), provider, session);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        return new AuthService(provider, _crypto, session, tokenStore, bootstrapper, cache, refresher);
    }

    // 预置一条加密缓存(直接写 store,不经过 session 解锁/加锁,避免 Dispose 清零测试仍要复用的 fixture.UserKey)。
    // 头部绑定与解锁流程一致:AccountId=Email、Origin=Server、Generation=1(持久化会话过渡值)。
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
        // profile.Id 须等于登录/解锁租约 AccountId(过渡期为 Email),否则同步/缓存门禁拒绝。
        return new SyncResponse("sync", new ProfileDto(Email, Email, "Me", null, null), [], [dto]);
    }

    private string FreshSyncJson(SymmetricCryptoKey userKey) =>
        JsonSerializer.Serialize(BuildSync("Fresh", userKey), ApiJsonContext.Default.SyncResponse);

    private static string DevicesJson() =>
        """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}""";

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
