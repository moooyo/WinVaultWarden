using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using App.Services;
using Core.Models;
using Xunit;

namespace App.Tests;

public sealed class LoopbackSsoAuthorizationServiceTests
{
    [Fact]
    public void BuildAuthorizationUri_UsesOfficialDesktopSsoShape()
    {
        var uri = LoopbackSsoAuthorizationService.BuildAuthorizationUri(
            new ServerEndpoint("https://vault.example/base"),
            "http://localhost:8065",
            "state-value",
            "challenge-value",
            "User+tag@example.com");

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("vault.example", uri.Host);
        Assert.StartsWith("/base/#/sso?", uri.PathAndQuery + uri.Fragment);
        var query = ParseSsoFragment(uri);
        Assert.Equal("desktop", query["clientId"]);
        Assert.Equal("http://localhost:8065", query["redirectUri"]);
        Assert.Equal("state-value", query["state"]);
        Assert.Equal("challenge-value", query["codeChallenge"]);
        Assert.Equal("User+tag@example.com", query["email"]);
    }

    [Fact]
    public void BuildAuthorizationUri_PreservesInlineOrganizationIdentifier()
    {
        var uri = LoopbackSsoAuthorizationService.BuildAuthorizationUri(
            new ServerEndpoint("https://vault.example/base"), "http://localhost:8065",
            "state-value", "challenge-value", "me@example.com", "  family & work  ");
        var query = ParseSsoFragment(uri);
        Assert.Equal("family & work", query["identifier"]);
        Assert.Equal("state-value", query["state"]);
        Assert.Equal("challenge-value", query["codeChallenge"]);
    }

    [Fact]
    public async Task ReopenPendingBrowser_ReusesOriginalPkceRequestAndStopsAfterCancellation()
    {
        var launcher = new RecordingLauncher();
        var service = new LoopbackSsoAuthorizationService(launcher, TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var authorization = service.AuthorizeAsync(ServerEndpoints.ForSelfHosted("https://vault.example"),
            "me@example.com", "family", null, cancellation.Token);
        for (var attempt = 0; attempt < 100 && launcher.Uris.Count == 0; attempt++)
            await Task.Yield();

        Assert.True(await service.ReopenPendingBrowserAsync());
        Assert.Equal(2, launcher.Uris.Count);
        Assert.Equal(launcher.Uris[0], launcher.Uris[1]);
        Assert.Equal("family", ParseSsoFragment(launcher.Uris[0])["identifier"]);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authorization);
        Assert.False(await service.ReopenPendingBrowserAsync());
        Assert.Equal(2, launcher.Uris.Count);
    }

    [Fact]
    public async Task AuthorizeAsync_AcceptsExactStateWithIdentifierAndReturnsPkceMaterial()
    {
        var launcher = new CallbackLauncher(async uri =>
        {
            var query = ParseSsoFragment(uri);
            var callbackUri = query["redirectUri"] + "/?code=returned-code&state="
                + Uri.EscapeDataString(query["state"] + "_identifier=example-org")
                + "&iss=" + Uri.EscapeDataString("https://vault.example");
            using var http = new HttpClient();
            using var response = await http.GetAsync(callbackUri, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });
        var service = new LoopbackSsoAuthorizationService(launcher, TimeSpan.FromSeconds(10));

        var result = await service.AuthorizeAsync(
            ServerEndpoints.ForSelfHosted("https://vault.example"),
            "me@example.com",
            CancellationToken.None);
        await launcher.CallbackTask;

        Assert.Equal("returned-code", result.Code);
        Assert.Equal("example-org", result.OrganizationIdentifier);
        Assert.StartsWith("http://localhost:", result.RedirectUri);
        Assert.InRange(new Uri(result.RedirectUri).Port, 8065, 8070);
        Assert.Equal(64, result.CodeVerifier.Length);
        Assert.Equal(64, result.State.Split("_identifier=", 2)[0].Length);

        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(result.CodeVerifier)));
        Assert.Equal(challenge, ParseSsoFragment(launcher.LaunchedUri!)["codeChallenge"]);
    }

    [Fact]
    public async Task AuthorizeAsync_RejectsWrongStateAndContinuesWaitingForValidCallback()
    {
        var launcher = new CallbackLauncher(async uri =>
        {
            var query = ParseSsoFragment(uri);
            using var http = new HttpClient();
            using var wrongIssuer = await http.GetAsync(
                query["redirectUri"] + "/?code=bad-code&state="
                    + Uri.EscapeDataString(query["state"])
                    + "&iss=" + Uri.EscapeDataString("https://evil.example"),
                CancellationToken.None);
            Assert.Equal(HttpStatusCode.BadRequest, wrongIssuer.StatusCode);

            using var rejected = await http.GetAsync(
                query["redirectUri"] + "/?code=bad-code&state=wrong-state",
                CancellationToken.None);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

            using var accepted = await http.GetAsync(
                query["redirectUri"] + "/?code=good-code&state="
                    + Uri.EscapeDataString(query["state"])
                    + "&iss=" + Uri.EscapeDataString("https://vault.example"),
                CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        });
        var service = new LoopbackSsoAuthorizationService(launcher, TimeSpan.FromSeconds(10));

        var result = await service.AuthorizeAsync(
            ServerEndpoints.ForSelfHosted("https://vault.example"),
            "me@example.com",
            CancellationToken.None);
        await launcher.CallbackTask;

        Assert.Equal("good-code", result.Code);
    }

    [Fact]
    public async Task AuthorizeAsync_SlowCallbackReportsTimeoutInsteadOfCancellation()
    {
        var launcher = new CallbackLauncher(async uri =>
        {
            var redirect = new Uri(ParseSsoFragment(uri)["redirectUri"]);
            using var client = new TcpClient();
            await client.ConnectAsync("localhost", redirect.Port);
            await client.GetStream().WriteAsync("GET /"u8.ToArray());
            await Task.Delay(300);
        });
        var service = new LoopbackSsoAuthorizationService(launcher, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => service.AuthorizeAsync(
            ServerEndpoints.ForSelfHosted("https://vault.example"),
            "me@example.com",
            CancellationToken.None));
        await launcher.CallbackTask;
    }

    private static Dictionary<string, string> ParseSsoFragment(Uri uri)
    {
        var marker = uri.Fragment.IndexOf('?');
        Assert.True(marker >= 0);
        return uri.Fragment[(marker + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => WebUtility.UrlDecode(pair[0]),
                pair => WebUtility.UrlDecode(pair.Length == 2 ? pair[1] : string.Empty));
    }

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed class RecordingLauncher : IExternalUriLauncher
    {
        public List<Uri> Uris { get; } = [];
        public Task<bool> LaunchAsync(Uri uri, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Uris.Add(uri);
            return Task.FromResult(true);
        }
    }

    private sealed class CallbackLauncher(Func<Uri, Task> callback) : IExternalUriLauncher
    {
        public Uri? LaunchedUri { get; private set; }
        public Task CallbackTask { get; private set; } = Task.CompletedTask;

        public Task<bool> LaunchAsync(Uri uri, CancellationToken ct = default)
        {
            LaunchedUri = uri;
            CallbackTask = Task.Run(() => callback(uri), ct);
            return Task.FromResult(true);
        }
    }
}
