using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Models;

namespace Core.Passkeys;

public sealed record WebAuthnMakeCredentialRequest(
    string Origin, string Challenge, string? RpId, string? RpName,
    string UserId, string? UserName, string? UserDisplayName,
    IReadOnlyList<int> Algorithms, bool UserVerification, bool CredProps, bool CrossOrigin = false);

public sealed record WebAuthnRegistrationResult(CipherFido2Credential Credential, PasskeyCreateAttestationPayload Payload);

// credProps 扩展结果的强类型表示,避免匿名类型走反射序列化(破坏 AOT)。
public sealed record CredPropsResult([property: JsonPropertyName("rk")] bool Rk);

public sealed record CredPropsExtensions([property: JsonPropertyName("credProps")] CredPropsResult CredProps);

public static class WebAuthnRegistrationService
{
    private const int Es256 = -7;

    public static WebAuthnRegistrationResult CreateAttestation(WebAuthnMakeCredentialRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        WebAuthnCrypto.DecodeBase64Url(request.Challenge);   // validate
        WebAuthnCrypto.DecodeBase64Url(request.UserId);       // validate

        if (!request.Algorithms.Contains(Es256))
            throw new InvalidOperationException("Only ES256 passkeys are supported.");

        var effectiveRpId = string.IsNullOrWhiteSpace(request.RpId) ? HostFromOrigin(request.Origin) : request.RpId;
        if (!IsRpIdAllowedForOrigin(effectiveRpId, request.Origin))
            throw new InvalidOperationException("The requested rpId is not valid for the origin.");

        var (pkcs8, cose, spki) = WebAuthnCrypto.GenerateEs256KeyPair();
        var credentialId = RandomNumberGenerator.GetBytes(16);
        var authData = WebAuthnCrypto.BuildAttestedAuthenticatorData(
            effectiveRpId, 0, userPresent: true, userVerified: request.UserVerification,
            aaguid: new byte[16], credentialId, cose);
        var attestationObject = WebAuthnCrypto.BuildNoneAttestationObject(authData);
        var clientDataJson = JsonSerializer.SerializeToUtf8Bytes(
            new ClientDataJson("webauthn.create", request.Challenge, request.Origin, request.CrossOrigin),
            PasskeyJsonContext.Default.ClientDataJson);

        JsonElement? extensions = request.CredProps
            ? JsonSerializer.SerializeToElement(
                new CredPropsExtensions(new CredPropsResult(true)),
                PasskeyJsonContext.Default.CredPropsExtensions)
            : null;

        var credentialIdB64 = WebAuthnCrypto.EncodeBase64Url(credentialId);
        var credential = new CipherFido2Credential(
            CredentialId: credentialIdB64,
            KeyType: "public-key",
            KeyAlgorithm: "ECDSA",
            KeyCurve: "P-256",
            KeyValue: pkcs8,
            RpId: effectiveRpId,
            UserHandle: request.UserId,
            UserName: request.UserName,
            Counter: 0,
            RpName: request.RpName,
            UserDisplayName: request.UserDisplayName,
            Discoverable: true,
            CreationDate: DateTimeOffset.UtcNow);

        var payload = new PasskeyCreateAttestationPayload(
            credentialIdB64,
            WebAuthnCrypto.EncodeBase64Url(clientDataJson),
            WebAuthnCrypto.EncodeBase64Url(attestationObject),
            WebAuthnCrypto.EncodeBase64Url(authData),
            WebAuthnCrypto.EncodeBase64Url(spki),
            Es256,
            ["internal"],
            extensions);

        return new WebAuthnRegistrationResult(credential, payload);
    }

    private static string HostFromOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : throw new InvalidOperationException("Origin must be an absolute URI.");

    private static bool IsRpIdAllowedForOrigin(string rpId, string origin)
    {
        var host = HostFromOrigin(origin);
        return string.Equals(host, rpId, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{rpId}", StringComparison.OrdinalIgnoreCase);
    }
}
