using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public sealed class WebAuthnTwoFactorProtocolTests
{
    private static readonly ServerEndpoints Endpoints =
        ServerEndpoints.ForSelfHosted("https://vault.example");

    [Fact]
    public void ParseChallenge_ValidatesAndCanonicalizesProviderData()
    {
        var challengeBytes = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var credentialId = new byte[] { 9, 8, 7, 6 };
        var providerData = ChallengeJson(
            WebAuthnCrypto.EncodeBase64Url(challengeBytes) + "=",
            "vault.example",
            WebAuthnCrypto.EncodeBase64Url(credentialId),
            "https://vault.example/app-id.json",
            timeout: 500_000);

        var challenge = WebAuthnTwoFactorProtocol.ParseChallenge(providerData, Endpoints);

        Assert.Equal(WebAuthnCrypto.EncodeBase64Url(challengeBytes), challenge.Challenge);
        Assert.Equal("vault.example", challenge.RpId);
        Assert.Equal(credentialId, Assert.Single(challenge.AllowCredentialIds));
        Assert.Equal("discouraged", challenge.UserVerification);
        Assert.Equal(120_000, challenge.TimeoutMilliseconds);
        Assert.Equal("https://vault.example", challenge.Origin);
        Assert.Equal("https://vault.example/app-id.json", challenge.U2fAppId);
    }

    [Theory]
    [InlineData("other.example", "https://vault.example/app-id.json")]
    [InlineData("vault.example", "https://evil.example/app-id.json")]
    public void ParseChallenge_RejectsRpOrAppIdOutsideConfiguredOrigin(
        string rpId,
        string appId)
    {
        var json = ChallengeJson(
            WebAuthnCrypto.EncodeBase64Url(new byte[32]),
            rpId,
            WebAuthnCrypto.EncodeBase64Url([1, 2, 3]),
            appId);

        Assert.Throws<WebAuthnTwoFactorException>(() =>
            WebAuthnTwoFactorProtocol.ParseChallenge(json, Endpoints));
    }

    [Fact]
    public void ParseChallenge_RejectsInsecureNonLoopbackOrigin()
    {
        var json = ChallengeJson(
            WebAuthnCrypto.EncodeBase64Url(new byte[32]),
            "vault.example",
            WebAuthnCrypto.EncodeBase64Url([1, 2, 3]),
            null);

        Assert.Throws<WebAuthnTwoFactorException>(() =>
            WebAuthnTwoFactorProtocol.ParseChallenge(
                json,
                ServerEndpoints.ForSelfHosted("http://vault.example")));
    }

    [Fact]
    public void BuildClientDataJson_UsesConfiguredOriginAndWebAuthnGetType()
    {
        var challenge = WebAuthnTwoFactorProtocol.ParseChallenge(
            ChallengeJson(
                WebAuthnCrypto.EncodeBase64Url(new byte[32]),
                "vault.example",
                WebAuthnCrypto.EncodeBase64Url([1, 2, 3]),
                null),
            Endpoints);

        using var document = JsonDocument.Parse(
            WebAuthnTwoFactorProtocol.BuildClientDataJson(challenge));

        Assert.Equal("webauthn.get", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(challenge.Challenge, document.RootElement.GetProperty("challenge").GetString());
        Assert.Equal("https://vault.example", document.RootElement.GetProperty("origin").GetString());
        Assert.False(document.RootElement.GetProperty("crossOrigin").GetBoolean());
    }

    [Fact]
    public void SerializeToken_UsesVaultwardenWebAuthnWireNames()
    {
        var assertion = new WebAuthnTwoFactorAssertion(
            [1, 2, 3],
            Enumerable.Repeat((byte)4, 37).ToArray(),
            Encoding.UTF8.GetBytes("{\"type\":\"webauthn.get\"}"),
            [5, 6, 7],
            null,
            true);

        var token = WebAuthnTwoFactorProtocol.SerializeToken(assertion);
        using var document = JsonDocument.Parse(token);
        var root = document.RootElement;
        var id = WebAuthnCrypto.EncodeBase64Url(assertion.CredentialId);

        Assert.Equal(id, root.GetProperty("id").GetString());
        Assert.Equal(id, root.GetProperty("rawId").GetString());
        Assert.Equal("public-key", root.GetProperty("type").GetString());
        Assert.Equal(
            WebAuthnCrypto.EncodeBase64Url(assertion.AuthenticatorData),
            root.GetProperty("response").GetProperty("authenticatorData").GetString());
        Assert.Equal(
            WebAuthnCrypto.EncodeBase64Url(assertion.ClientDataJson),
            root.GetProperty("response").GetProperty("clientDataJson").GetString());
        Assert.Equal(
            WebAuthnCrypto.EncodeBase64Url(assertion.Signature),
            root.GetProperty("response").GetProperty("signature").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("response").GetProperty("userHandle").ValueKind);
        Assert.True(root.GetProperty("extensions").GetProperty("appid").GetBoolean());
    }

    private static string ChallengeJson(
        string challenge,
        string rpId,
        string credentialId,
        string? appId,
        int timeout = 60_000)
    {
        var extensions = new JsonObject();
        if (appId is not null)
            extensions["appid"] = appId;
        return new JsonObject
        {
            ["challenge"] = challenge,
            ["rpId"] = rpId,
            ["timeout"] = timeout,
            ["userVerification"] = "discouraged",
            ["allowCredentials"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "public-key",
                    ["id"] = credentialId,
                    ["transports"] = new JsonArray("usb", "nfc"),
                },
            },
            ["extensions"] = extensions,
        }.ToJsonString();
    }
}
