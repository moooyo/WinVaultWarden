using System.Net;
using System.Text;
using Api.Dtos;
using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class AuthRequestTokenWireTests
{
    private const string RequestId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string DeviceIdentifier = "11111111-2222-3333-4444-555555555555";
    private const string AccessCode = "1234567890123456789012345";

    [Fact]
    public void Factory_CreatesCanonicalPasswordGrant()
    {
        var request = ConnectTokenRequest.AuthRequest(
            "me@example.com",
            AccessCode,
            RequestId.ToUpperInvariant(),
            DeviceIdentifier.ToUpperInvariant(),
            "WinVaultWarden");

        Assert.Equal("password", request.GrantType);
        Assert.Equal("me@example.com", request.Username);
        Assert.Equal(AccessCode, request.PasswordHash);
        Assert.Equal(RequestId, request.AuthRequestId);
        Assert.Equal(DeviceIdentifier, request.DeviceIdentifier);
        Assert.Equal("api offline_access", request.Scope);
        Assert.Equal("desktop", request.ClientId);
        Assert.Equal("6", request.DeviceType);
        Assert.Null(request.TwoFactor);
    }

    [Fact]
    public async Task Connect_SendsExactFormAndRetainsIdDuringTwoFactorRetry()
    {
        var handler = new RecordingHandler();
        using var api = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));
        var request = ConnectTokenRequest.AuthRequest(
            "me@example.com",
            AccessCode,
            RequestId,
            DeviceIdentifier,
            "WinVaultWarden") with
        {
            TwoFactor = new TwoFactorPayload(0, "123456", true),
        };

        var result = await api.ConnectTokenAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.IsType<ConnectTokenResult.Success>(result);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/identity/connect/token", handler.RequestUri!.AbsolutePath);
        var form = ParseForm(handler.Body!);
        Assert.Equal("password", form["grant_type"]);
        Assert.Equal("api offline_access", form["scope"]);
        Assert.Equal("desktop", form["client_id"]);
        Assert.Equal("me@example.com", form["username"]);
        Assert.Equal(AccessCode, form["password"]);
        Assert.Equal(RequestId, form["authRequest"]);
        Assert.Equal(DeviceIdentifier, form["deviceIdentifier"]);
        Assert.Equal("WinVaultWarden", form["deviceName"]);
        Assert.Equal("6", form["deviceType"]);
        Assert.Equal("0", form["twoFactorProvider"]);
        Assert.Equal("123456", form["twoFactorToken"]);
        Assert.Equal("1", form["twoFactorRemember"]);
        Assert.False(form.ContainsKey("auth_request"));
        Assert.False(form.ContainsKey("newDeviceOtp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}")]
    [InlineData("not-a-guid")]
    public void Factory_RejectsInvalidRequestId(string requestId)
    {
        Assert.Throws<ArgumentException>(() => ConnectTokenRequest.AuthRequest(
            "me@example.com",
            AccessCode,
            requestId,
            DeviceIdentifier,
            "WinVaultWarden"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("11111111222233334444555555555555")]
    [InlineData("not-a-guid")]
    public void Factory_RejectsInvalidDeviceIdentifier(string deviceIdentifier)
    {
        Assert.Throws<ArgumentException>(() => ConnectTokenRequest.AuthRequest(
            "me@example.com",
            AccessCode,
            RequestId,
            deviceIdentifier,
            "WinVaultWarden"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("12345678901234567890123456")]
    public void Factory_RequiresTwentyFiveCharacterAccessCode(string accessCode)
    {
        Assert.Throws<ArgumentException>(() => ConnectTokenRequest.AuthRequest(
            "me@example.com",
            accessCode,
            RequestId,
            DeviceIdentifier,
            "WinVaultWarden"));
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
                pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')),
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
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access","Key":null,"PrivateKey":null,"Kdf":0,"KdfIterations":600000}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
