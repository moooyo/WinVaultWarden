using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class SyncOfflineRecoveryTests
{
    private const string Email = "me@example.com";
    private const string Server = "https://vault.example";
    private const string AccessToken = "e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln";
    private const string RefreshJson =
        """{"access_token":"e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"rotated-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}""";
    private const string SyncJson =
        """{"object":"sync","profile":{"id":"me@example.com","email":"me@example.com","name":"Review","securityStamp":"stamp-1"},"folders":[],"ciphers":[]}""";

    [Fact]
    public async Task OfflineSync_RefreshesBeforeReadAndMarksSnapshotFresh()
    {
        using var fixture = new Fixture();
        fixture.Handler.Enqueue(request =>
        {
            Assert.Equal("/identity/connect/token", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, RefreshJson);
        });
        EnqueueSync(fixture.Handler);

        await fixture.Service.SyncAsync(fixture.Lease, TestContext.Current.CancellationToken);

        Assert.Equal("/identity/connect/token", fixture.Handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("grant_type=refresh_token", fixture.Handler.Bodies[0], StringComparison.Ordinal);
        Assert.False(fixture.Session.DataStale);
        Assert.Equal(1, fixture.CacheStore.SaveCount);
        Assert.True(fixture.TokenStore.TryLoad(out var persisted));
        Assert.Equal("rotated-refresh", persisted.RefreshToken);
    }

    [Fact]
    public async Task OfflineSync_ConcurrentRequestsShareRecovery()
    {
        using var fixture = new Fixture();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Enqueue((_, _) =>
        {
            refreshStarted.TrySetResult();
            return response.Task;
        });
        EnqueueSync(fixture.Handler);
        EnqueueSync(fixture.Handler);

        var first = fixture.Service.SyncAsync(fixture.Lease, TestContext.Current.CancellationToken);
        await refreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = fixture.Service.SyncAsync(fixture.Lease, TestContext.Current.CancellationToken);
        Assert.Single(fixture.Handler.Requests);
        response.SetResult(FakeHttpMessageHandler.Json(HttpStatusCode.OK, RefreshJson));
        await Task.WhenAll(first, second);

        Assert.Single(fixture.Handler.Requests, request =>
            request.RequestUri!.AbsolutePath == "/identity/connect/token");
        Assert.Equal(2, fixture.Handler.Requests.Count(request =>
            request.RequestUri!.AbsolutePath == "/api/sync"));
        Assert.False(fixture.Session.DataStale);
    }

    [Fact]
    public async Task OfflineSync_RefreshFailureDoesNotSendUnauthenticatedSync()
    {
        using var fixture = new Fixture();
        fixture.Handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.ServiceUnavailable,
            """{"message":"Temporary service failure"}"""));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Service.SyncAsync(fixture.Lease, TestContext.Current.CancellationToken));

        Assert.Single(fixture.Handler.Requests);
        Assert.Equal("/identity/connect/token", fixture.Handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.True(fixture.Session.IsCurrent(fixture.Lease));
        Assert.True(fixture.Session.DataStale);
        Assert.NotNull(fixture.Session.UserKey);
        Assert.Null(fixture.Session.AccessToken);
        Assert.Equal(0, fixture.CacheStore.SaveCount);
    }

    [Fact]
    public async Task OfflineSync_WithoutRefresherDoesNotDispatch()
    {
        using var fixture = new Fixture(includeRefresher: false);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Service.SyncAsync(fixture.Lease, TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Handler.Requests);
        Assert.True(fixture.Session.DataStale);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineSync_LockOrAccountSwitchDuringRefreshNeverSendsSync(bool switchAccount)
    {
        using var fixture = new Fixture();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Enqueue((_, _) =>
        {
            refreshStarted.TrySetResult();
            return response.Task;
        });

        var running = fixture.Service.SyncAsync(fixture.Lease, TestContext.Current.CancellationToken);
        await refreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (switchAccount)
        {
            fixture.Session.BeginAccountSession("other", "other@example.com", new ServerOrigin(Server));
            fixture.Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            fixture.Session.SetTokens("other-access", "other-refresh");
        }
        else
        {
            fixture.Session.Lock();
        }
        response.SetResult(FakeHttpMessageHandler.Json(HttpStatusCode.OK, RefreshJson));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(0, fixture.CacheStore.SaveCount);
        if (switchAccount)
        {
            Assert.Equal("other", fixture.Session.CaptureLease().AccountId);
            Assert.Equal("other-access", fixture.Session.AccessToken);
        }
        else
        {
            Assert.Equal(VaultState.Locked, fixture.Session.State);
            Assert.Null(fixture.Session.UserKey);
            Assert.Null(fixture.Session.AccessToken);
        }
    }

    private static void EnqueueSync(FakeHttpMessageHandler handler)
    {
        handler.Enqueue(request =>
        {
            Assert.Equal("/api/sync", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(AccessToken, request.Headers.Authorization?.Parameter);
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson);
        });
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly VaultApiClientProvider _clients;
        public FakeHttpMessageHandler Handler { get; } = new();
        public VaultSession Session { get; } = new();
        public MemoryTokenStore TokenStore { get; } = new();
        public FakeCacheStore CacheStore { get; } = new();
        public VaultSessionLease Lease { get; }
        public SyncService Service { get; }

        public Fixture(bool includeRefresher = true)
        {
            Lease = Session.BeginAccountSession(Email, Email, new ServerOrigin(Server));
            Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            Session.SetRefreshTokenOnly("saved-refresh");
            Session.TryMarkDataStale(Lease);
            TokenStore.Save(new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                Email, Lease.Endpoints, Lease.Generation, Email, "review-device",
                "saved-refresh", "2.protected", KdfType.Pbkdf2, 600_000, null, null));
            var decryptor = new VaultDecryptor(new CryptoService());
            var cache = new VaultCacheService(CacheStore, decryptor, Session);
            ITokenRefresher? refresher = null;
            _clients = new VaultApiClientProvider(
                new ApiClientFactory(_ => Handler),
                Session,
                (lease, failed, ct) => refresher!.TryRefreshAsync(lease, failed, ct));
            refresher = new TokenRefreshService(_clients, Session, TokenStore);
            Service = new SyncService(
                _clients, decryptor, Session, cache, TokenStore,
                tokenRefresher: includeRefresher ? refresher : null);
        }

        public void Dispose()
        {
            Session.Logout();
            _clients.Dispose();
        }
    }
}
