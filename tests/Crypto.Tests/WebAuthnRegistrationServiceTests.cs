using Core.Passkeys;
using Xunit;

namespace Crypto.Tests;

public class WebAuthnRegistrationServiceTests
{
    private static WebAuthnMakeCredentialRequest Req(IReadOnlyList<int> algs, bool uv = true, bool credProps = true) =>
        new("https://github.com", "AQIDBA", "github.com", "GitHub", "dXNlcklk", "octo", "Octo Cat", algs, uv, credProps);

    [Fact]
    public void CreateAttestation_Es256_ProducesStorableCredential_AndAttestationPayload()
    {
        var result = WebAuthnRegistrationService.CreateAttestation(Req([-7]));

        Assert.Equal("public-key", result.Credential.KeyType);
        Assert.Equal("ECDSA", result.Credential.KeyAlgorithm);
        Assert.Equal("P-256", result.Credential.KeyCurve);
        Assert.Equal("github.com", result.Credential.RpId);
        Assert.Equal("dXNlcklk", result.Credential.UserHandle);
        Assert.True(result.Credential.Discoverable);
        Assert.False(string.IsNullOrWhiteSpace(result.Credential.KeyValue));
        Assert.False(string.IsNullOrWhiteSpace(result.Credential.CredentialId));

        Assert.Equal(result.Credential.CredentialId, result.Payload.CredentialId);
        Assert.Equal(-7, result.Payload.PublicKeyAlgorithm);
        Assert.Contains("internal", result.Payload.Transports);
        Assert.False(string.IsNullOrWhiteSpace(result.Payload.AttestationObject));
        Assert.False(string.IsNullOrWhiteSpace(result.Payload.AuthData));
        Assert.False(string.IsNullOrWhiteSpace(result.Payload.PublicKey));

        // clientDataJSON: type webauthn.create, challenge passed through.
        var cdj = System.Text.Encoding.UTF8.GetString(WebAuthnCrypto.DecodeBase64Url(result.Payload.ClientDataJson));
        Assert.Contains("\"type\":\"webauthn.create\"", cdj);
        Assert.Contains("\"challenge\":\"AQIDBA\"", cdj);
        // attestationObject parses.
        var r = new System.Formats.Cbor.CborReader(WebAuthnCrypto.DecodeBase64Url(result.Payload.AttestationObject));
        Assert.Equal(3, r.ReadStartMap());
    }

    [Fact]
    public void CreateAttestation_WithoutEs256_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => WebAuthnRegistrationService.CreateAttestation(Req([-257]))); // RS256 only
    }

    [Fact]
    public void CreateAttestation_RpIdNotValidForOrigin_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebAuthnRegistrationService.CreateAttestation(
                new("https://evil.com", "AQIDBA", "github.com", "GitHub", "dXNlcklk", "octo", "Octo", [-7], true, false)));
    }
}
