using Core.Passkeys;
using Core.Services;
using Xunit;

namespace Crypto.Tests;

public class WebAuthnRegistrationServiceTests
{
    private static readonly PasskeyApprovalEvidence VerifiedApproval =
        new(Approved: true, UserPresent: true, UserVerified: true);

    private static WebAuthnMakeCredentialRequest Req(
        IReadOnlyList<int> algorithms,
        PasskeyApprovalEvidence? evidence = null,
        bool credProps = true) =>
        new(
            "https://github.com",
            "AQIDBA",
            "github.com",
            "GitHub",
            "dXNlcklk",
            "octo",
            "Octo Cat",
            algorithms,
            evidence ?? VerifiedApproval,
            credProps);

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

        Assert.True(Guid.TryParseExact(result.Credential.CredentialId, "D", out _));
        Assert.False(result.Payload.CredentialId.StartsWith(
            CredentialId.Base64Prefix,
            StringComparison.Ordinal));
        Assert.True(CredentialId.StorageIdEqualsWire(
            result.Credential.CredentialId,
            result.Payload.CredentialId));
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

        var authData = WebAuthnCrypto.DecodeBase64Url(result.Payload.AuthData);
        var credentialIdLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(
            authData.AsSpan(53, 2));
        Assert.Equal(
            CredentialId.DecodeWire(result.Payload.CredentialId),
            authData.AsSpan(55, credentialIdLength).ToArray());
    }

    [Fact]
    public void CreateAttestation_UnapprovedEvidence_AndWithoutEs256_ThrowsApprovalFirst()
    {
        var request = Req(
            [-257],
            new PasskeyApprovalEvidence(false, false, false));

        var error = Assert.Throws<InvalidOperationException>(() =>
            WebAuthnRegistrationService.CreateAttestation(request));

        Assert.Equal("Passkey approval is required.", error.Message);
    }

    [Fact]
    public void CreateAttestation_RpIdNotValidForOrigin_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebAuthnRegistrationService.CreateAttestation(
                new WebAuthnMakeCredentialRequest(
                    "https://evil.com",
                    "AQIDBA",
                    "github.com",
                    "GitHub",
                    "dXNlcklk",
                    "octo",
                    "Octo",
                    [-7],
                    VerifiedApproval,
                    CredProps: false)));
    }

    [Theory]
    [InlineData(false, false, 0x58)]
    [InlineData(true, false, 0x59)]
    [InlineData(true, true, 0x5d)]
    public void CreateAttestation_MapsApprovalEvidenceToAtUpUvFlags(
        bool userPresent,
        bool userVerified,
        int expectedFlags)
    {
        var evidence = new PasskeyApprovalEvidence(
            Approved: true,
            UserPresent: userPresent,
            UserVerified: userVerified);

        var result = WebAuthnRegistrationService.CreateAttestation(
            Req([-7], evidence));

        var authData = WebAuthnCrypto.DecodeBase64Url(result.Payload.AuthData);
        Assert.Equal(expectedFlags, (int)authData[32]);
    }

    [Fact]
    public void CreateAttestation_UnapprovedEvidence_Throws()
    {
        var request = Req(
            [-7],
            new PasskeyApprovalEvidence(false, false, false));

        var error = Assert.Throws<InvalidOperationException>(() =>
            WebAuthnRegistrationService.CreateAttestation(request));

        Assert.Equal("Passkey approval is required.", error.Message);
    }

    [Fact]
    public void CreateAttestation_UserVerifiedWithoutPresence_Throws()
    {
        var request = Req(
            [-7],
            new PasskeyApprovalEvidence(true, false, true));

        Assert.Throws<ArgumentException>(() =>
            WebAuthnRegistrationService.CreateAttestation(request));
    }

    [Fact]
    public void NewlyRegisteredCredential_PreservesBackupFlagsWhenUsedForAssertion()
    {
        var registration = WebAuthnRegistrationService.CreateAttestation(Req([-7]));
        var assertion = WebAuthnAssertionService.CreateAssertion(
            registration.Credential,
            new WebAuthnGetAssertionRequest("https://github.com", "BQYHCA", "github.com",
                [registration.Payload.CredentialId], VerifiedApproval));

        var registrationFlags = WebAuthnCrypto.DecodeBase64Url(registration.Payload.AuthData)[32];
        var assertionFlags = WebAuthnCrypto.DecodeBase64Url(assertion.AuthenticatorData)[32];
        Assert.Equal(0x18, registrationFlags & 0x18);
        Assert.Equal(registrationFlags & 0x18, assertionFlags & 0x18);
    }
}
