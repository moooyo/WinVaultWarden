using System.Net;
using Api;
using Core.Models;
using Core.Session;
using Xunit;

namespace Api.Tests;

public sealed class ServerRoutingTests
{
    [Fact]
    public async Task OfficialEnvironment_RoutesIdentityAndApiToIndependentHosts()
    {
        var transport = new RecordingHandler(request =>
            request.RequestUri!.Host.StartsWith("identity", StringComparison.Ordinal)
                ? Json("""{"kdf":0,"kdfIterations":600000}""")
                : Json("""{"object":"sync","profile":null,"folders":[],"ciphers":[]}"""));
        using var client = new ApiClient(new HttpClient(transport), ServerEndpoints.BitwardenUs);

        await client.PreloginAsync("me@example.com", TestContext.Current.CancellationToken);
        await client.GetSyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal("identity.bitwarden.com", transport.Requests[0].RequestUri!.Host);
        Assert.Equal("/accounts/prelogin", transport.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("api.bitwarden.com", transport.Requests[1].RequestUri!.Host);
        Assert.Equal("/sync", transport.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task SelfHostedEnvironment_PreservesBasePathForEveryRoute()
    {
        var transport = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("prelogin", StringComparison.Ordinal)
                ? Json("""{"kdf":0,"kdfIterations":600000}""")
                : Json("""{"object":"sync","profile":null,"folders":[],"ciphers":[]}"""));
        using var client = new ApiClient(
            new HttpClient(transport),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));

        await client.PreloginAsync("me@example.com", TestContext.Current.CancellationToken);
        await client.GetSyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/warden/identity/accounts/prelogin", transport.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/warden/api/sync", transport.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task BearerAudience_RequiresExactApiEndpointPathBoundary()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/warden");
        var binding = new AccessTokenBinding("account", endpoints, 1, "token-a");
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var handler = new AuthHeaderHandler(
            endpoints,
            () => binding,
            (_, _) => Task.FromResult(false))
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);

        using var apiResponse = await http.GetAsync(
            endpoints.Api.Resolve("sync"),
            TestContext.Current.CancellationToken);
        using var apiaryResponse = await http.GetAsync(
            "https://vault.example/warden/apiary/sync",
            TestContext.Current.CancellationToken);
        using var identityResponse = await http.GetAsync(
            endpoints.Identity.Resolve("connect/token"),
            TestContext.Current.CancellationToken);
        binding = new AccessTokenBinding(
            "account",
            ServerEndpoints.ForSelfHosted("https://vault.example/other"),
            1,
            "token-b");
        using var wrongEndpointResponse = await http.GetAsync(
            endpoints.Api.Resolve("sync"),
            TestContext.Current.CancellationToken);

        Assert.Equal("token-a", transport.Requests[0].Headers.Authorization?.Parameter);
        Assert.Null(transport.Requests[1].Headers.Authorization);
        Assert.Null(transport.Requests[2].Headers.Authorization);
        Assert.Null(transport.Requests[3].Headers.Authorization);
    }

    [Fact]
    public async Task DataUrl_BearerFollowsExactApiAudienceForAbsoluteAndRelativeUrls()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/warden");
        var binding = new AccessTokenBinding("account", endpoints, 1, "token-a");
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]),
        });
        using var handler = new AuthHeaderHandler(
            endpoints,
            () => binding,
            (_, _) => Task.FromResult(false))
        {
            InnerHandler = transport,
        };
        using var client = new ApiClient(new HttpClient(handler), endpoints);

        await using var matchingDownload = await client.OpenAttachmentDownloadAsync(
            endpoints.Api.Resolve("objects/file").AbsoluteUri,
            TestContext.Current.CancellationToken);
        await using var externalDownload = await client.OpenAttachmentDownloadAsync(
            "https://objects.example/file",
            TestContext.Current.CancellationToken);
        await using var relativeDownload = await client.OpenAttachmentDownloadAsync(
            "download/file",
            TestContext.Current.CancellationToken);

        Assert.Equal("token-a", transport.Requests[0].Headers.Authorization?.Parameter);
        Assert.Null(transport.Requests[1].Headers.Authorization);
        Assert.Equal("token-a", transport.Requests[2].Headers.Authorization?.Parameter);
        Assert.Equal(
            "/warden/api/download/file",
            transport.Requests[2].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task UnauthorizedRefresh_ReceivesBindingAttachedToFailedRequest()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var current = new AccessTokenBinding("account", endpoints, 1, "old-token");
        AccessTokenBinding? observedFailure = null;
        var call = 0;
        var transport = new RecordingHandler(_ =>
        {
            call++;
            if (call == 1)
            {
                current = current with { Token = "new-token" };
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var handler = new AuthHeaderHandler(
            endpoints,
            () => current,
            (failed, _) =>
            {
                observedFailure = failed;
                return Task.FromResult(true);
            })
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);

        using var response = await http.GetAsync(
            endpoints.Api.Resolve("sync"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("old-token", observedFailure!.Token);
        Assert.Equal("old-token", transport.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal("new-token", transport.Requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task CloneRequestAsync_DropsAuthorizationAndZerosReplayContentOnDispose()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://vault.example/api/ciphers")
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "old-token");
        request.Headers.TryAddWithoutValidation("X-Test", "preserved");

        AuthHeaderHandler.ZeroingByteArrayContent replayContent;
        using (var clone = await AuthHeaderHandler.CloneRequestAsync(
                   request,
                   TestContext.Current.CancellationToken))
        {
            Assert.Null(clone.Headers.Authorization);
            Assert.Equal("preserved", clone.Headers.GetValues("X-Test").Single());
            replayContent = Assert.IsType<AuthHeaderHandler.ZeroingByteArrayContent>(
                clone.Content);
            Assert.False(replayContent.IsDisposed);
            Assert.False(replayContent.IsCleared);
        }

        Assert.True(replayContent.IsDisposed);
        Assert.True(replayContent.IsCleared);
    }

    [Fact]
    public async Task UnauthorizedResponse_CloneThrows_DisposesOriginalResponse()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var binding = new AccessTokenBinding("account", endpoints, 1, "old-token");
        var responseContent = new TrackingContent();
        var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = responseContent,
        };
        var refreshCalls = 0;
        var transport = new RecordingHandler(_ => unauthorized);
        using var handler = new AuthHeaderHandler(
            endpoints,
            () => binding,
            (_, _) =>
            {
                Interlocked.Increment(ref refreshCalls);
                return Task.FromResult(true);
            })
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoints.Api.Resolve("ciphers"))
        {
            Content = new ThrowingContent(),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            http.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.True(responseContent.IsDisposed);
        Assert.Equal(0, refreshCalls);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task UnauthorizedResponse_RefreshThrows_DisposesOriginalResponse()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var binding = new AccessTokenBinding("account", endpoints, 1, "old-token");
        var responseContent = new TrackingContent();
        var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = responseContent,
        };
        var transport = new RecordingHandler(_ => unauthorized);
        using var handler = new AuthHeaderHandler(
            endpoints,
            () => binding,
            (_, _) => Task.FromException<bool>(
                new InvalidOperationException("Refresh failed.")))
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoints.Api.Resolve("ciphers"))
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            http.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.True(responseContent.IsDisposed);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task UnauthorizedResponse_RefreshIsCanceled_DisposesOriginalResponse()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var binding = new AccessTokenBinding("account", endpoints, 1, "old-token");
        var responseContent = new TrackingContent();
        var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = responseContent,
        };
        using var refreshCancellation = new CancellationTokenSource();
        refreshCancellation.Cancel();
        var transport = new RecordingHandler(_ => unauthorized);
        using var handler = new AuthHeaderHandler(
            endpoints,
            () => binding,
            (_, _) => Task.FromCanceled<bool>(refreshCancellation.Token))
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoints.Api.Resolve("ciphers"))
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            http.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.True(responseContent.IsDisposed);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task SuccessfulRetry_DisposesAndZerosReplayContent()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var current = new AccessTokenBinding("account", endpoints, 1, "old-token");
        var responseContent = new TrackingContent();
        var unauthorized = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = responseContent,
        };
        AuthHeaderHandler.ZeroingByteArrayContent? replayContent = null;
        bool? replayWasClearedBeforeSend = null;
        var call = 0;
        var transport = new RecordingHandler(request =>
        {
            if (Interlocked.Increment(ref call) == 1)
                return unauthorized;

            replayContent = Assert.IsType<AuthHeaderHandler.ZeroingByteArrayContent>(
                request.Content);
            replayWasClearedBeforeSend = replayContent.IsCleared;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var handler = new AuthHeaderHandler(
            endpoints,
            () => current,
            (_, _) =>
            {
                current = current with { Token = "new-token" };
                return Task.FromResult(true);
            })
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            endpoints.Api.Resolve("ciphers"))
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };

        using var response = await http.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(responseContent.IsDisposed);
        Assert.False(replayWasClearedBeforeSend);
        Assert.NotNull(replayContent);
        Assert.True(replayContent.IsDisposed);
        Assert.True(replayContent.IsCleared);
        Assert.Equal("new-token", transport.Requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task SuccessfulRefresh_WhenBindingExpiresBeforeRetry_DoesNotSendAnonymousRequest()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        AccessTokenBinding? current = new(
            "account",
            endpoints,
            1,
            "old-token");
        var refreshEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completeRefresh = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retryBindingEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRetryBinding = new ManualResetEventSlim();
        var providerCalls = 0;
        var transportCalls = 0;
        var transport = new RecordingHandler(_ =>
            Interlocked.Increment(ref transportCalls) == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK));
        using var handler = new AuthHeaderHandler(
            endpoints,
            () =>
            {
                if (Interlocked.Increment(ref providerCalls) == 2)
                {
                    retryBindingEntered.TrySetResult();
                    if (!releaseRetryBinding.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Retry binding gate was not released.");
                }
                return Volatile.Read(ref current);
            },
            (_, _) =>
            {
                refreshEntered.TrySetResult();
                return completeRefresh.Task;
            })
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);

        var send = http.PostAsync(
            endpoints.Api.Resolve("ciphers"),
            new StringContent("{\"name\":\"encrypted-secret\"}"),
            TestContext.Current.CancellationToken);

        await refreshEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        completeRefresh.SetResult(true);
        await retryBindingEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Interlocked.Exchange(ref current, null);
        releaseRetryBinding.Set();

        using var response = await send.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, providerCalls);
        Assert.Single(transport.Requests);
        Assert.Equal("old-token", transport.Requests[0].Headers.Authorization!.Parameter);
    }

    [Theory]
    [InlineData("//evil.example/file")]
    [InlineData("///evil.example/file")]
    [InlineData(@"\evil.example\share")]
    [InlineData("https:evil.example/file")]
    [InlineData("ftp://evil.example/file")]
    [InlineData("file:///tmp/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user@example.com/file")]
    [InlineData("https://objects.example/file#fragment")]
    public async Task DataUrl_RejectsAmbiguousOrUnsafeReferences(string input)
    {
        var transport = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new ApiClient(
            new HttpClient(transport),
            ServerEndpoints.ForSelfHosted("https://vault.example/warden"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.OpenAttachmentDownloadAsync(
                input,
                TestContext.Current.CancellationToken));

        Assert.Empty(transport.Requests);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TrackingContent() : ByteArrayContent([1])
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.FromException(new InvalidOperationException("Clone failed."));

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("Clone failed."));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
