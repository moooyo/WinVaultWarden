using System.Net;
using Api;
using Core.Models;
using Core.Session;
using Xunit;

namespace Api.Tests;

public sealed class ApiClientIsolationTests
{
    [Fact]
    public void DefaultTransport_DisablesAutomaticRedirectsForEveryRole()
    {
        var field = typeof(ApiClientFactory).GetField(
            "_transportFactory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var factory = Assert.IsType<Func<bool, HttpMessageHandler>>(
            field!.GetValue(new ApiClientFactory()));

        using var anonymous = Assert.IsType<HttpClientHandler>(factory(false));
        using var authenticated = Assert.IsType<HttpClientHandler>(factory(true));

        Assert.False(anonymous.AllowAutoRedirect);
        Assert.False(authenticated.AllowAutoRedirect);
    }

    [Fact]
    public async Task AnonymousIdentityClient_NeverCarriesExistingBearer()
    {
        var transport = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            "{\"kdf\":0,\"kdfIterations\":600000}"));
        var factory = new ApiClientFactory(_ => transport);
        var client = factory.CreateAnonymous<IIdentityApiClient>(
            new ServerOrigin("https://b.example"));

        await client.PreloginAsync("b@example.com");

        Assert.Null(transport.Requests.Single().Headers.Authorization);
    }

    [Fact]
    public async Task ServerConfigRole_ResolvesForAnonymousAndAuthenticatedTransports()
    {
        var anonymousTransport = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var anonymousFactory = new ApiClientFactory(_ => anonymousTransport);
        var anonymous = anonymousFactory.CreateAnonymous<IServerConfigApiClient>(
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        await anonymous.GetConfigAsync(TestContext.Current.CancellationToken);

        var authenticatedTransport = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var authenticatedFactory = new ApiClientFactory(_ => authenticatedTransport);
        var aggregate = authenticatedFactory.CreateAuthenticated(
            endpoints,
            () => new AccessTokenBinding("account", endpoints, 1, "token-a"),
            (_, _) => Task.FromResult(false));
        var authenticated = Assert.IsAssignableFrom<IServerConfigApiClient>(aggregate);

        await authenticated.GetConfigAsync(TestContext.Current.CancellationToken);

        Assert.Null(anonymousTransport.Requests.Single().Headers.Authorization);
        Assert.Equal("token-a", authenticatedTransport.Requests.Single().Headers.Authorization?.Parameter);
        Assert.Equal("/api/config", authenticatedTransport.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task AuthenticatedClient_AttachesOnlyToMatchingOrigin()
    {
        var transport = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var origin = new ServerOrigin("https://a.example");
        var binding = new AccessTokenBinding("account-a", origin, 4, "token-a");
        var handler = new AuthHeaderHandler(
            origin,
            () => binding,
            _ => Task.FromResult(false))
        {
            InnerHandler = transport,
        };
        using var http = new HttpClient(handler);

        await http.GetAsync("https://a.example/api/sync");
        await http.GetAsync("https://b.example/api/sync");

        Assert.Equal("token-a", transport.Requests[0].Headers.Authorization?.Parameter);
        Assert.Null(transport.Requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task CrossOriginRedirect_IsNotAutomaticallyFollowed()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://b.example/api/sync") },
        };
        var transport = new RecordingHandler(_ => redirect);
        var factory = new ApiClientFactory(_ => transport);
        var auth = factory.CreateAuthenticated(
            new ServerOrigin("https://a.example"),
            () => new AccessTokenBinding(
                "account-a", new ServerOrigin("https://a.example"), 1, "token-a"),
            _ => Task.FromResult(false));

        await Assert.ThrowsAsync<VaultApiException>(() => auth.GetSyncAsync());

        Assert.Single(transport.Requests);
        Assert.Equal("a.example", transport.Requests[0].RequestUri!.Host);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(response(request));
        }
    }
}
