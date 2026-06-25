using System.Security.Cryptography;
using System.Text.Json;
using Core.Models;
using Core.Passkeys;
using Xunit;

namespace Crypto.Tests;

public class WebAuthnAssertionServiceTests
{
    [Fact]
    public void CreateAssertion_SignsChallengeForMatchingCredential()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = Credential(key);
        var request = new WebAuthnGetAssertionRequest(
            "https://login.github.com",
            "AQIDBA",
            "github.com",
            [credential.CredentialId!],
            UserVerified: true);

        var assertion = WebAuthnAssertionService.CreateAssertion(credential, request);

        Assert.Equal(credential.CredentialId, assertion.CredentialId);
        Assert.Equal(6u, assertion.SignatureCounter);
        Assert.Equal(credential.UserHandle, assertion.UserHandle);

        var authData = WebAuthnCrypto.DecodeBase64Url(assertion.AuthenticatorData);
        Assert.Equal(0x05, authData[32]);
        Assert.Equal(new byte[] { 0, 0, 0, 6 }, authData[33..37]);

        var clientDataJson = WebAuthnCrypto.DecodeBase64Url(assertion.ClientDataJson);
        using var clientData = JsonDocument.Parse(clientDataJson);
        Assert.Equal("webauthn.get", clientData.RootElement.GetProperty("type").GetString());
        Assert.Equal("AQIDBA", clientData.RootElement.GetProperty("challenge").GetString());
        Assert.Equal("https://login.github.com", clientData.RootElement.GetProperty("origin").GetString());

        var signedData = authData.Concat(SHA256.HashData(clientDataJson)).ToArray();
        Assert.True(key.VerifyData(
            signedData,
            WebAuthnCrypto.DecodeBase64Url(assertion.Signature),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void CreateAssertion_RejectsCredentialOutsideAllowList()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = Credential(key);
        var request = new WebAuthnGetAssertionRequest(
            "https://github.com",
            "AQIDBA",
            "github.com",
            ["not-the-credential"],
            UserVerified: false);

        Assert.Throws<InvalidOperationException>(() =>
            WebAuthnAssertionService.CreateAssertion(credential, request));
    }

    [Fact]
    public void CreateAssertion_RejectsRpIdThatDoesNotMatchOrigin()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = Credential(key);
        var request = new WebAuthnGetAssertionRequest(
            "https://github.com",
            "AQIDBA",
            "evil.example",
            [credential.CredentialId!],
            UserVerified: false);

        Assert.Throws<InvalidOperationException>(() =>
            WebAuthnAssertionService.CreateAssertion(credential, request));
    }

    private static CipherFido2Credential Credential(ECDsa key) => new(
        WebAuthnCrypto.EncodeBase64Url([1, 2, 3, 4]),
        "public-key",
        "ECDSA",
        "P-256",
        WebAuthnCrypto.EncodeBase64Url(key.ExportPkcs8PrivateKey()),
        "github.com",
        WebAuthnCrypto.EncodeBase64Url([5, 6, 7, 8]),
        "octo@example.com",
        5,
        "GitHub",
        "Octo",
        true,
        DateTimeOffset.Parse("2026-06-24T00:00:00Z"));
}
