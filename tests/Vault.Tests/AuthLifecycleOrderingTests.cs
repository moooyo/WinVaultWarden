using System.Security.Cryptography;
using System.Text;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// Task 7:认证生命周期门禁——刷新/登出在 await 期间切账户的竞态,以及 Lock/Logout 的失效优先顺序。
public class AuthLifecycleOrderingTests
{
    [Fact]
    public async Task TryRefreshAsync_LogoutDuringAwait_DoesNotPersistRotatedToken()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ConnectTokenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new ControlledIdentityApiClient(async _ =>
        {
            entered.TrySetResult();
            return await response.Task;
        });
        var session = SessionWithPersistedTokens(out var lease, out var store);
        var refresher = new TokenRefreshService(
            FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity),
            session,
            store);

        var running = refresher.TryRefreshAsync(lease, CancellationToken.None);
        await entered.Task;
        session.Logout();
        store.Clear();
        response.SetResult(new ConnectTokenResult.Success(new TokenResponse(
            TestAccessToken("account-a"),
            "new-r",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            600_000)));

        Assert.False(await running);
        Assert.False(store.TryLoad(out _));
        Assert.Null(session.AccessToken);
    }

    [Fact]
    public async Task TryRefreshAsync_LockDuringAwaitPreservesRotatedRefreshToken()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ConnectTokenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new ControlledIdentityApiClient(async token =>
        {
            entered.TrySetResult();
            return await response.Task.WaitAsync(token);
        });
        var session = SessionWithPersistedTokens(out var lease, out var store);
        var cache = CreateCache(new FakeCacheStore(), session);
        var auth = CreateAuthServiceForLifecycle(session, store, cache);
        var refresher = new TokenRefreshService(
            FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity),
            session,
            store);

        var running = refresher.TryRefreshAsync(
            lease,
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await auth.LockAsync(TestContext.Current.CancellationToken);
        response.SetResult(new ConnectTokenResult.Success(new TokenResponse(
            TestAccessToken("account-a"),
            "new-r",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            600_000)));

        Assert.False(await running);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal("new-r", persisted.RefreshToken);
        Assert.Equal(session.Generation, persisted.Generation);
    }

    [Fact]
    public async Task TryRefreshAsync_OfflineUnlockDescendantAdoptsLateRotation()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ConnectTokenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new ControlledIdentityApiClient(async token =>
        {
            entered.TrySetResult();
            return await response.Task.WaitAsync(token);
        });
        var session = SessionWithPersistedTokens(out var lease, out var store);
        var refresher = new TokenRefreshService(
            FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity),
            session,
            store);

        var running = refresher.TryRefreshAsync(
            lease,
            TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var lockedLease = Assert.IsType<VaultSessionLease>(session.Lock());
        Assert.True(session.TryCommit(lockedLease, () =>
        {
            Assert.True(store.TryLoad(out var persisted));
            store.Save(persisted with
            {
                Generation = lockedLease.Generation,
            });
            session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            session.SetRefreshTokenOnly("old-refresh");
        }));
        response.SetResult(new ConnectTokenResult.Success(new TokenResponse(
            TestAccessToken("account-a"),
            "new-r",
            3600,
            "Bearer",
            "api offline_access",
            null,
            null,
            KdfType.Pbkdf2,
            600_000)));

        Assert.False(await running);
        Assert.Equal(VaultState.Unlocked, session.State);
        Assert.Equal(TestAccessToken("account-a"), session.AccessToken);
        Assert.Equal("new-r", session.RefreshToken);
        Assert.True(store.TryLoad(out var updated));
        Assert.Equal("new-r", updated.RefreshToken);
        Assert.Equal(lockedLease.Generation, updated.Generation);
        session.Logout();
    }

    [Fact]
    public async Task TryRefreshAsync_PersistedOriginMismatch_MakesNoRequest()
    {
        var identity = new ControlledIdentityApiClient(_ =>
            throw new InvalidOperationException("must not call identity API"));
        var session = SessionWithPersistedTokens(out var lease, out var store);
        Assert.True(store.TryLoad(out var persisted));
        store.Save(persisted with
        {
            Origin = new ServerOrigin("https://b.example"),
        });
        var refresher = new TokenRefreshService(
            FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity),
            session,
            store);

        var result = await refresher.TryRefreshAsync(
            lease, CancellationToken.None);

        Assert.False(result);
        Assert.Equal(0, identity.ConnectCallCount);
        Assert.Equal("old-access", session.AccessToken);
    }

    [Fact]
    public async Task TryRefreshAsync_SwitchToB_DoesNotCommitAOrLockB()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ConnectTokenResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new ControlledIdentityApiClient(async _ =>
        {
            entered.TrySetResult();
            return await response.Task;
        });
        var session = SessionWithPersistedTokens(out var leaseA, out var store);
        var refresher = new TokenRefreshService(
            FakeVaultApiClientProvider.ForAnonymous<IIdentityApiClient>(identity),
            session,
            store);

        var running = refresher.TryRefreshAsync(leaseA, CancellationToken.None);
        await entered.Task;
        var leaseB = session.BeginAccountSession(
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"));
        response.SetResult(new ConnectTokenResult.Error(
            "invalid refresh token",
            400,
            "invalid_grant"));

        Assert.False(await running);
        Assert.True(session.IsCurrent(leaseB));
        Assert.NotEqual(VaultState.Locked, session.State);
    }

    [Fact]
    public async Task Logout_InvalidatesLeaseBeforeClearingStores()
    {
        var session = SessionWithPersistedTokens(out var lease, out var tokenStore);
        var cacheStore = new FakeCacheStore();
        var cache = CreateCache(cacheStore, session);
        var observedCurrentDuringClear = true;
        var order = new List<string>();
        tokenStore.BeforeClear = () =>
        {
            observedCurrentDuringClear = session.IsCurrent(lease);
            order.Add("token-clear");
        };
        cacheStore.BeforeClear = () => order.Add("cache-clear");
        var service = CreateAuthServiceForLifecycle(session, tokenStore, cache);

        await service.LogoutAsync();

        Assert.False(observedCurrentDuringClear);
        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.Equal(["token-clear", "cache-clear"], order);
    }

    [Fact]
    public async Task Login_BootstrapFromACompletesAfterBLogin_DoesNotLockOrClearB()
    {
        const string emailA = "a@example.com";
        var crypto = new CryptoService();
        var masterKey = crypto.DeriveMasterKey(
            "password-a",
            emailA,
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        using var stretchedKey = crypto.StretchMasterKey(masterKey);
        CryptographicOperations.ZeroMemory(masterKey);
        using var userKeyA = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        var protectedUserKey = crypto.Encrypt(userKeyA.FullKey, stretchedKey).ToString();

        var identity = new ControlledIdentityApiClient(_ =>
            Task.FromResult<ConnectTokenResult>(new ConnectTokenResult.Success(
                new TokenResponse(
                    TestAccessToken("account-a"),
                    "refresh-a",
                    3600,
                    "Bearer",
                    "api offline_access",
                    protectedUserKey,
                    null,
                    KdfType.Pbkdf2,
                    600_000))));
        var syncEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new ControlledReadonlyApiClient(async _ =>
        {
            syncEntered.TrySetResult();
            await releaseSync.Task;
            return new SyncResponse(
                "sync",
                new ProfileDto("account-a", emailA, "A", null, null)
                {
                    SecurityStamp = "stamp-1",
                },
                [],
                []);
        });
        var session = new VaultSession();
        var tokenStore = new MemoryTokenStore();
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IIdentityApiClient>(identity)
            .AddAuthenticated<IReadonlyApiClient>(read);
        var decryptor = new VaultDecryptor(crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var sync = new SyncService(provider, decryptor, session, cache);
        var bootstrapper = new VaultBootstrapper(sync, provider, session);
        var auth = new AuthService(
            provider,
            crypto,
            session,
            tokenStore,
            bootstrapper,
            cache,
            refresher: null!);
        var attemptA = auth.BeginAuthenticationAttempt();

        var loginA = auth.LoginAsync(
            attemptA,
            "https://a.example",
            emailA,
            "password-a",
            TestContext.Current.CancellationToken);
        await syncEntered.Task;

        var leaseB = session.BeginAccountSession(
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"));
        session.SetTokens("access-b", "refresh-b");
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Repeat((byte)7, 64).ToArray()));
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            leaseB.AccountId,
            leaseB.Origin,
            leaseB.Generation,
            "b@example.com",
            "device-b",
            "refresh-b",
            "2.protected-b",
            KdfType.Pbkdf2,
            600_000,
            null,
            null));
        releaseSync.SetResult();

        Assert.IsType<AuthResult.Failure>(await loginA);
        Assert.True(session.IsUnlocked(leaseB));
        Assert.Equal("access-b", session.AccessToken);
        Assert.True(tokenStore.TryLoad(out var persistedB));
        Assert.Equal("account-b", persistedB.AccountId);
    }

    private static string TestAccessToken(string accountId)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        return $"{Encode("{\"alg\":\"none\"}")}.{Encode($"{{\"sub\":\"{accountId}\"}}")}.signature";
    }

    private sealed class RecordingTokenStore : ITokenStore
    {
        private PersistedSession? _persisted;
        public Action? BeforeClear { get; set; }

        public bool TryLoad(out PersistedSession session)
        {
            session = _persisted!;
            return _persisted is not null;
        }

        public void Save(PersistedSession session) => _persisted = session;

        public void Clear()
        {
            BeforeClear?.Invoke();
            _persisted = null;
        }
    }

    private static VaultSession SessionWithPersistedTokens(
        out VaultSessionLease lease,
        out RecordingTokenStore store)
    {
        var session = new VaultSession();
        var origin = new ServerOrigin("https://a.example");
        lease = session.BeginAccountSession("account-a", "a@example.com", origin);
        session.TryCommit(lease, () =>
            session.SetTokens("old-access", "old-refresh"));
        store = new RecordingTokenStore();
        store.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Origin,
            lease.Generation,
            "a@example.com",
            "device-a",
            "old-refresh",
            "2.protected",
            KdfType.Pbkdf2,
            600_000,
            null,
            null));
        return session;
    }

    private static VaultCacheService CreateCache(
        FakeCacheStore store,
        VaultSession session) =>
        new(store, new VaultDecryptor(new CryptoService()), session);

    private static AuthService CreateAuthServiceForLifecycle(
        VaultSession session,
        ITokenStore tokenStore,
        VaultCacheService cache) =>
        new(
            clients: null!,
            crypto: new CryptoService(),
            session: session,
            tokenStore: tokenStore,
            bootstrapper: null!,
            cache: cache,
            refresher: null!);
}
