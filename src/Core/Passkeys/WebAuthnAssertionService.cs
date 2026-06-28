using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Models;

namespace Core.Passkeys;

public sealed record ClientDataJson(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("crossOrigin")] bool CrossOrigin);

public sealed record WebAuthnGetAssertionRequest(
    string Origin,
    string Challenge,
    string? RpId,
    IReadOnlyList<string> AllowedCredentialIds,
    bool UserVerified);

public sealed record WebAuthnAssertionResult(
    string CredentialId,
    string AuthenticatorData,
    string ClientDataJson,
    string Signature,
    string? UserHandle,
    uint SignatureCounter);

public static class WebAuthnAssertionService
{
    public static WebAuthnAssertionResult CreateAssertion(
        CipherFido2Credential credential,
        WebAuthnGetAssertionRequest request)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);

        var credentialId = Required(credential.CredentialId, "credential id");
        var keyValue = Required(credential.KeyValue, "private key");
        var credentialRpId = Required(credential.RpId, "rpId");
        WebAuthnCrypto.DecodeBase64Url(credentialId);
        WebAuthnCrypto.DecodeBase64Url(request.Challenge);

        var effectiveRpId = string.IsNullOrWhiteSpace(request.RpId) ? HostFromOrigin(request.Origin) : request.RpId;
        if (!IsRpIdAllowedForOrigin(effectiveRpId, request.Origin))
            throw new InvalidOperationException("The requested rpId is not valid for the origin.");
        if (!string.Equals(credentialRpId, effectiveRpId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The credential rpId does not match the request rpId.");
        if (request.AllowedCredentialIds.Count > 0
            && !request.AllowedCredentialIds.Contains(credentialId, StringComparer.Ordinal))
            throw new InvalidOperationException("The credential is not in allowCredentials.");

        var signatureCounter = credential.Counter is > 0
            ? checked((uint)credential.Counter.Value + 1)
            : 0u;
        var authenticatorData = WebAuthnCrypto.BuildAuthenticatorData(
            effectiveRpId,
            signatureCounter,
            userPresent: true,
            userVerified: request.UserVerified);
        var clientDataJson = BuildClientDataJson(request);
        var signature = WebAuthnCrypto.SignAssertion(keyValue, authenticatorData, clientDataJson);

        return new WebAuthnAssertionResult(
            credentialId,
            WebAuthnCrypto.EncodeBase64Url(authenticatorData),
            WebAuthnCrypto.EncodeBase64Url(clientDataJson),
            WebAuthnCrypto.EncodeBase64Url(signature),
            credential.UserHandle,
            signatureCounter);
    }

    private static byte[] BuildClientDataJson(WebAuthnGetAssertionRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new ClientDataJson("webauthn.get", request.Challenge, request.Origin, false),
            PasskeyJsonContext.Default.ClientDataJson);

    private static string HostFromOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("Origin must be an absolute URI.");

        return uri.Host;
    }

    private static bool IsRpIdAllowedForOrigin(string rpId, string origin)
    {
        var host = HostFromOrigin(origin);
        return string.Equals(host, rpId, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{rpId}", StringComparison.OrdinalIgnoreCase);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Passkey credential {name} is required.")
            : value;
}
