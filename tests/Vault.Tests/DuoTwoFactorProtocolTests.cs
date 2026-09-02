using System.Text;
using System.Text.Json;
using Core.Models;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public sealed class DuoTwoFactorProtocolTests
{
    private static readonly ServerEndpoints Endpoints =
        ServerEndpoints.ForSelfHosted("https://vault.example");
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void ParseChallenge_ValidatesDuoRequestAndExtractsState()
    {
        var providerData = ProviderData();

        var challenge = DuoTwoFactorProtocol.ParseChallenge(providerData, Endpoints, Now);

        Assert.Equal("https", challenge.AuthorizationUri.Scheme);
        Assert.Equal("api-test.duosecurity.com", challenge.AuthorizationUri.Host);
        Assert.Equal("state1234567890ABCDEF", challenge.State);
        Assert.Equal(
            "https://vault.example/duo-redirect-connector.html?client=desktop",
            challenge.CallbackUri.AbsoluteUri);
    }

    [Fact]
    public void ParseChallenge_AcceptsMatchingHostWithCurrentOidcShape()
    {
        var challenge = DuoTwoFactorProtocol.ParseChallenge(
            ProviderData(host: "api-test.duosecurity.com"),
            Endpoints,
            Now);

        Assert.Equal("api-test.duosecurity.com", challenge.AuthorizationUri.IdnHost);
    }

    [Fact]
    public void ParseChallenge_RejectsMismatchedHostWithCurrentOidcShape()
    {
        Assert.Throws<DuoTwoFactorException>(() =>
            DuoTwoFactorProtocol.ParseChallenge(
                ProviderData(host: "api-other.duosecurity.com"),
                Endpoints,
                Now));
    }

    [Fact]
    public void ParseChallenge_IdentifiesLegacyIframeShapeExplicitly()
    {
        Assert.Throws<DuoLegacyUnsupportedException>(() =>
            DuoTwoFactorProtocol.ParseChallenge(LegacyProviderData(), Endpoints, Now));
    }

    [Fact]
    public void ParseChallenge_IdentifiesPaddedLegacyIframeSignatureExplicitly()
    {
        const string paddedSignature =
            "TX|bWU=|0000000000000000000000000000000000000000:"
            + "APP|bWU=|1111111111111111111111111111111111111111";

        Assert.Throws<DuoLegacyUnsupportedException>(() =>
            DuoTwoFactorProtocol.ParseChallenge(
                LegacyProviderData(paddedSignature),
                Endpoints,
                Now));
    }

    [Fact]
    public void ParseChallenge_RejectsMixedOidcAndLegacyShape()
    {
        var exception = Assert.Throws<DuoTwoFactorException>(() =>
            DuoTwoFactorProtocol.ParseChallenge(
                ProviderData(
                    host: "api-test.duosecurity.com",
                    signature: LegacySignature),
                Endpoints,
                Now));

        Assert.IsNotType<DuoLegacyUnsupportedException>(exception);
    }

    [Theory]
    [InlineData("https://evil.example/oauth/v1/authorize")]
    [InlineData("http://api-test.duosecurity.com/oauth/v1/authorize")]
    [InlineData("https://api-test.duosecurity.com/not-authorize")]
    public void ParseChallenge_RejectsUntrustedAuthorizationEndpoint(string endpoint)
    {
        Assert.Throws<DuoTwoFactorException>(() =>
            DuoTwoFactorProtocol.ParseChallenge(
                ProviderData(authorizationEndpoint: endpoint),
                Endpoints,
                Now));
    }

    [Fact]
    public void ParseChallenge_RejectsCallbackOutsideConfiguredWebVault()
    {
        Assert.Throws<DuoTwoFactorException>(() =>
            DuoTwoFactorProtocol.ParseChallenge(
                ProviderData(redirectUri: "https://evil.example/duo-redirect-connector.html?client=desktop"),
                Endpoints,
                Now));
    }

    [Fact]
    public void TryCreateToken_RequiresExactConnectorOriginPathAndState()
    {
        var challenge = DuoTwoFactorProtocol.ParseChallenge(ProviderData(), Endpoints, Now);
        var valid = new Uri(
            "https://vault.example/duo-redirect-connector.html?client=desktop&code=duo-code&state=state1234567890ABCDEF");

        Assert.True(DuoTwoFactorProtocol.TryCreateToken(challenge, valid, out var token));
        Assert.Equal("duo-code|state1234567890ABCDEF", token);
        Assert.False(DuoTwoFactorProtocol.TryCreateToken(
            challenge,
            new Uri("https://vault.example/duo-redirect-connector.html?client=desktop&code=duo-code&state=wrong"),
            out _));
        Assert.False(DuoTwoFactorProtocol.TryCreateToken(
            challenge,
            new Uri("https://evil.example/duo-redirect-connector.html?client=desktop&code=duo-code&state=state1234567890ABCDEF"),
            out _));
    }

    private static string ProviderData(
        string authorizationEndpoint = "https://api-test.duosecurity.com/oauth/v1/authorize",
        string? redirectUri = null,
        long? expiration = null,
        string? host = null,
        string? signature = null)
    {
        const string clientId = "DIXXXXXXXXXXXXXXXXXX";
        var authorizationUri = new Uri(authorizationEndpoint);
        redirectUri ??= "https://vault.example/duo-redirect-connector.html?client=desktop";
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "HS512",
            typ = "JWT",
        }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            response_type = "code",
            scope = "openid",
            exp = expiration ?? Now.AddMinutes(5).ToUnixTimeSeconds(),
            client_id = clientId,
            redirect_uri = redirectUri,
            state = "state1234567890ABCDEF",
            duo_uname = "me@example.com",
            iss = clientId,
            aud = authorizationUri.GetLeftPart(UriPartial.Authority),
            nonce = "nonce1234567890ABCDEF",
        }));
        var request = $"{header}.{payload}.c2lnbmF0dXJl";
        var authUrl = authorizationEndpoint
            + "?response_type=code&client_id=" + Uri.EscapeDataString(clientId)
            + "&request=" + Uri.EscapeDataString(request);
        var data = new Dictionary<string, string>
        {
            ["AuthUrl"] = authUrl,
        };
        if (host is not null)
            data["Host"] = host;
        if (signature is not null)
            data["Signature"] = signature;
        return JsonSerializer.Serialize(data);
    }

    private const string LegacySignature =
        "TX|bWVleGFtcGxl|0000000000000000000000000000000000000000:"
        + "APP|bWVleGFtcGxl|1111111111111111111111111111111111111111";

    private static string LegacyProviderData(string signature = LegacySignature) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Host"] = "api-test.duosecurity.com",
            ["Signature"] = signature,
        });

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
