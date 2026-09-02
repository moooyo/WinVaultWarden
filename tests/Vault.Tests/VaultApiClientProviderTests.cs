using System.Net;
using Api;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class VaultApiClientProviderTests
{
    [Fact]
    public async Task OldAClient_AfterSwitchToB_CannotReadAToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"object\":\"sync\",\"profile\":null,\"folders\":[],\"ciphers\":[]}"));
        var session = new VaultSession();
        var a = session.BeginAccountSession(
            "account-a", "a@example.com", new ServerOrigin("https://a.example"));
        session.TryCommit(a, () => session.SetTokens("token-a", "refresh-a"));
        var provider = new VaultApiClientProvider(
            new ApiClientFactory(_ => handler),
            session,
            (_, _) => Task.FromResult(false));
        var oldClient = provider.GetAuthenticated<IReadonlyApiClient>(a);

        session.BeginAccountSession(
            "account-b", "b@example.com", new ServerOrigin("https://b.example"));
        await oldClient.GetSyncAsync(TestContext.Current.CancellationToken);

        Assert.Null(handler.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public void GetAuthenticated_RejectsStaleLeaseBeforeClientCreation()
    {
        var session = new VaultSession();
        var stale = session.BeginAccountSession(
            "a", "a@example.com", new ServerOrigin("https://a.example"));
        session.Lock();
        var provider = new VaultApiClientProvider(
            new ApiClientFactory(),
            session,
            (_, _) => Task.FromResult(false));

        Assert.Throws<OperationCanceledException>(() =>
            provider.GetAuthenticated<IReadonlyApiClient>(stale));
    }

    [Fact]
    public void GetAuthenticated_ResolvesDedicatedServerConfigRole()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account",
            "user@example.com",
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        using var provider = new VaultApiClientProvider(
            new ApiClientFactory(_ => new TrackingHandler()),
            session,
            (_, _, _) => Task.FromResult(false));

        var config = provider.GetAuthenticated<IServerConfigApiClient>(lease);

        Assert.IsAssignableFrom<IOriginBoundApiClient>(config);
    }

    [Fact]
    public async Task SessionSwitchDuringCreation_DiscardsStaleClientWithoutReplacingCurrent()
    {
        var enteredFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFactory = new ManualResetEventSlim();
        var handlers = new List<TrackingHandler>();
        var authenticatedCreations = 0;
        var factory = new ApiClientFactory(authenticated =>
        {
            var handler = new TrackingHandler();
            lock (handlers)
                handlers.Add(handler);

            if (authenticated && Interlocked.Increment(ref authenticatedCreations) == 2)
            {
                enteredFactory.TrySetResult();
                releaseFactory.Wait(TimeSpan.FromSeconds(5));
            }
            return handler;
        });
        var session = new VaultSession();
        var retained = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            ServerEndpoints.ForSelfHosted("https://vault.example/a"));
        var provider = new VaultApiClientProvider(
            factory,
            session,
            (_, _, _) => Task.FromResult(false));
        provider.GetAuthenticated<IReadonlyApiClient>(retained);

        var stale = session.BeginAccountSession(
            "account-b",
            "b@example.com",
            ServerEndpoints.ForSelfHosted("https://vault.example/b"));

        var staleResult = Task.Run(() => Record.Exception(() =>
            provider.GetAuthenticated<IReadonlyApiClient>(stale)));
        await enteredFactory.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var current = session.BeginAccountSession(
            "account-c",
            "c@example.com",
            ServerEndpoints.ForSelfHosted("https://vault.example/c"));
        releaseFactory.Set();

        Assert.IsType<OperationCanceledException>(await staleResult);
        Assert.False(handlers[0].Disposed);
        Assert.True(handlers[1].Disposed);

        var currentClient = provider.GetAuthenticated<IReadonlyApiClient>(current);
        Assert.Equal(current.Endpoints, Assert.IsAssignableFrom<IOriginBoundApiClient>(currentClient).Endpoints);
        Assert.True(handlers[0].Disposed);
        Assert.False(handlers[2].Disposed);

        provider.Dispose();
        Assert.True(handlers[2].Disposed);
    }

    [Fact]
    public void SwitchingSession_RetiresPreviousClientAfterCurrentReplacement()
    {
        var handlers = new List<TrackingHandler>();
        var factory = new ApiClientFactory(_ =>
        {
            var handler = new TrackingHandler();
            handlers.Add(handler);
            return handler;
        });
        var session = new VaultSession();
        var first = session.BeginAccountSession(
            "account-a", "a@example.com", new ServerOrigin("https://a.example"));
        var provider = new VaultApiClientProvider(
            factory,
            session,
            (_, _, _) => Task.FromResult(false));
        provider.GetAuthenticated<IReadonlyApiClient>(first);

        var second = session.BeginAccountSession(
            "account-b", "b@example.com", new ServerOrigin("https://b.example"));
        Assert.False(handlers[0].Disposed);

        provider.GetAuthenticated<IReadonlyApiClient>(second);

        Assert.Equal(2, handlers.Count);
        Assert.True(handlers[0].Disposed);
        Assert.False(handlers[1].Disposed);

        provider.Dispose();
        Assert.All(handlers, handler => Assert.True(handler.Disposed));
    }

    [Fact]
    public async Task SameStamp_ConcurrentResolution_ReusesSingleClient()
    {
        var handler = new TrackingHandler();
        var creations = 0;
        var factory = new ApiClientFactory(_ =>
        {
            Interlocked.Increment(ref creations);
            return handler;
        });
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a", "a@example.com", new ServerOrigin("https://a.example"));
        using var provider = new VaultApiClientProvider(
            factory,
            session,
            (_, _, _) => Task.FromResult(false));

        var resolutions = Enumerable.Range(0, 128)
            .Select(_ => Task.Run(() =>
                provider.GetAuthenticated<IReadonlyApiClient>(lease)))
            .ToArray();
        var clients = await Task.WhenAll(resolutions);

        Assert.Equal(1, Volatile.Read(ref creations));
        Assert.All(clients, client => Assert.Same(clients[0], client));
        Assert.False(handler.Disposed);
    }

    [Fact]
    public void RepeatedSessionRotation_RetainsAtMostOneAuthenticatedClient()
    {
        const int rotations = 512;
        var handlers = new List<TrackingHandler>();
        var factory = new ApiClientFactory(_ =>
        {
            var handler = new TrackingHandler();
            handlers.Add(handler);
            return handler;
        });
        var session = new VaultSession();
        using var provider = new VaultApiClientProvider(
            factory,
            session,
            (_, _, _) => Task.FromResult(false));

        for (var index = 0; index < rotations; index++)
        {
            var lease = session.BeginAccountSession(
                $"account-{index}",
                $"user-{index}@example.com",
                new ServerOrigin("https://vault.example"));

            provider.GetAuthenticated<IReadonlyApiClient>(lease);

            Assert.Equal(index + 1, handlers.Count);
            Assert.Equal(1, handlers.Count(handler => !handler.Disposed));
            Assert.Equal(index, handlers.Count(handler => handler.Disposed));
        }
    }

    [Fact]
    public void Dispose_IsIdempotent_AndRejectsFurtherResolution()
    {
        var handler = new TrackingHandler();
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a", "a@example.com", new ServerOrigin("https://a.example"));
        var provider = new VaultApiClientProvider(
            new ApiClientFactory(_ => handler),
            session,
            (_, _, _) => Task.FromResult(false));
        provider.GetAuthenticated<IReadonlyApiClient>(lease);

        provider.Dispose();
        provider.Dispose();

        Assert.Equal(1, handler.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() =>
            provider.GetAuthenticated<IReadonlyApiClient>(lease));
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public bool Disposed => DisposeCount != 0;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }
    }
}
