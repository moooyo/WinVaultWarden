using System.Net;
using System.Text;
using Api.Dtos;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class ApiKeyTokenRequestTests
{
    private const string AccountId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ClientId = "user." + AccountId;
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private const string ClientSecret = "synthetic-api-secret&with=escaped+characters";

    [Fact]
    public async Task PersonalApiKeyUsesTheExactClientCredentialsFormWithoutPasswordOrRefreshToken()
    {
        using var handler = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));

        var result = await api.ConnectTokenAsync(
            ConnectTokenRequest.UserApiKey(ClientId, ClientSecret, DeviceId, "WinVaultWarden"),
            TestContext.Current.CancellationToken);

        var success = Assert.IsType<ConnectTokenResult.Success>(result);
        Assert.True(string.IsNullOrEmpty(success.Token.RefreshToken));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/identity/connect/token", handler.RequestUri!.AbsolutePath);
        var form = ParseForm(handler.Body!);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal("api", form["scope"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal(ClientSecret, form["client_secret"]);
        Assert.Equal(DeviceId, form["deviceIdentifier"]);
        Assert.Equal("WinVaultWarden", form["deviceName"]);
        Assert.Equal("6", form["deviceType"]);
        Assert.False(form.ContainsKey("username"));
        Assert.False(form.ContainsKey("password"));
        Assert.False(form.ContainsKey("refresh_token"));
        Assert.False(form.ContainsKey("twoFactorToken"));
    }

    [Fact]
    public void DiagnosticStringDoesNotExposeTheApiSecret()
    {
        var request = ConnectTokenRequest.UserApiKey(ClientId, ClientSecret, DeviceId, "WinVaultWarden");

        Assert.DoesNotContain(ClientSecret, request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("organization.aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("user.not-a-guid")]
    [InlineData("user.00000000-0000-0000-0000-000000000000")]
    [InlineData("user.aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    public void OnlyCanonicalPersonalApiKeysAreAccepted(string clientId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ConnectTokenRequest.UserApiKey(clientId, ClientSecret, DeviceId, "WinVaultWarden"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n")]
    public void MissingApiSecretsAreRejected(string clientSecret)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ConnectTokenRequest.UserApiKey(ClientId, clientSecret, DeviceId, "WinVaultWarden"));
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .ToDictionary(
                pair => WebUtility.UrlDecode(pair[0]),
                pair => WebUtility.UrlDecode(pair[1]),
                StringComparer.Ordinal);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"access","expires_in":3600,"token_type":"Bearer","scope":"api","Key":null,"PrivateKey":null,"Kdf":0,"KdfIterations":600000}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
