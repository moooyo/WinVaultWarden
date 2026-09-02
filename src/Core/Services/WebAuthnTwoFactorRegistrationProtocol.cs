using System.Globalization;
using System.Net;
using System.Text.Json;
using Core.Models;
using Core.Passkeys;

namespace Core.Services;

public static class WebAuthnTwoFactorRegistrationProtocol
{
    private const int MaxCredentialCount = 64;
    private const int MaxCredentialIdBytes = 4096;
    private const int MaxAlgorithmCount = 32;

    public static WebAuthnRegistrationChallenge ParseChallenge(
        JsonElement options,
        ServerEndpoints endpoints,
        string? userVerificationToken = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (options.ValueKind != JsonValueKind.Object)
            throw InvalidChallenge();

        try
        {
            ValidateServerStatus(options);

            var originUri = new Uri(endpoints.WebVault.Origin.Value, UriKind.Absolute);
            if (!IsSecureWebAuthnOrigin(originUri))
                throw InvalidChallenge();

            var challengeBytes = DecodeBase64Url(
                RequiredString(options, "challenge", 4096),
                minimumBytes: 16,
                maximumBytes: 1024);
            var challenge = WebAuthnCrypto.EncodeBase64Url(challengeBytes);

            var rp = RequiredObject(options, "rp");
            var rpId = TryGetProperty(rp, "id", out var rpIdElement)
                && rpIdElement.ValueKind != JsonValueKind.Null
                    ? NormalizeHost(RequiredString(rpIdElement, 253))
                    : NormalizeHost(originUri.IdnHost);
            if (!string.Equals(rpId, NormalizeHost(originUri.IdnHost), StringComparison.Ordinal))
                throw InvalidChallenge();
            var rpName = RequiredString(rp, "name", 256);

            var user = RequiredObject(options, "user");
            var userId = DecodeBase64Url(
                RequiredString(user, "id", 512),
                minimumBytes: 1,
                maximumBytes: 64);
            var userName = RequiredString(user, "name", 256);
            var userDisplayName = RequiredString(user, "displayName", 256);

            var parameters = ParseParameters(options);
            var excludeCredentials = ParseExcludeCredentials(options);
            var timeout = ParseTimeout(options);
            var selection = ParseAuthenticatorSelection(options);
            var attestation = ParseAttestation(options);
            RejectUnsupportedExtensions(options);

            return new WebAuthnRegistrationChallenge(
                challenge,
                originUri.GetLeftPart(UriPartial.Authority),
                rpId,
                rpName,
                userId,
                userName,
                userDisplayName,
                parameters,
                excludeCredentials,
                timeout,
                selection.AuthenticatorAttachment,
                selection.ResidentKey,
                selection.RequireResidentKey,
                selection.UserVerification,
                attestation,
                userVerificationToken);
        }
        catch (WebAuthnTwoFactorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            FormatException or
            InvalidOperationException or
            OverflowException)
        {
            throw new WebAuthnTwoFactorException(
                "The WebAuthn registration challenge is invalid.",
                exception);
        }
    }

    public static void ValidateDeviceResponse(
        WebAuthnRegistrationChallenge challenge,
        WebAuthnRegistrationDeviceResponse response)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(response);
        if (response.CredentialId.Length is 0 or > MaxCredentialIdBytes
            || response.AttestationObject.Length is 0 or > 1024 * 1024
            || response.ClientDataJson.Length is 0 or > 1024 * 1024)
        {
            throw new WebAuthnTwoFactorException(
                "The WebAuthn registration response is invalid.");
        }
        if (challenge.ExcludeCredentials.Any(excluded =>
                excluded.Id.AsSpan().SequenceEqual(response.CredentialId)))
        {
            throw new WebAuthnTwoFactorException(
                "The authenticator returned an excluded WebAuthn credential.");
        }

        var seenTransports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in response.Transports)
        {
            var transport = value?.ToLowerInvariant();
            if (transport is not ("usb" or "nfc" or "ble" or "internal" or "hybrid" or "smart-card")
                || !seenTransports.Add(transport))
            {
                throw new WebAuthnTwoFactorException(
                    "The WebAuthn registration response contains an invalid transport.");
            }
        }

        try
        {
            using var document = JsonDocument.Parse(response.ClientDataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !string.Equals(
                    RequiredString(root, "type", 32),
                    "webauthn.create",
                    StringComparison.Ordinal)
                || !string.Equals(
                    CanonicalChallenge(RequiredString(root, "challenge", 4096)),
                    challenge.Challenge,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RequiredString(root, "origin", 2048),
                    challenge.Origin,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new WebAuthnTwoFactorException(
                    "The WebAuthn registration response does not match its challenge.");
            }
            if (TryGetProperty(root, "crossOrigin", out var crossOrigin)
                && (crossOrigin.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || crossOrigin.GetBoolean()))
            {
                throw new WebAuthnTwoFactorException(
                    "Cross-origin WebAuthn registration is not supported.");
            }
        }
        catch (WebAuthnTwoFactorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new WebAuthnTwoFactorException(
                "The WebAuthn registration response is invalid.",
                exception);
        }
    }

    private static IReadOnlyList<WebAuthnPublicKeyCredentialParameter> ParseParameters(
        JsonElement options)
    {
        if (!TryGetProperty(options, "pubKeyCredParams", out var values)
            || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() is 0 or > MaxAlgorithmCount)
        {
            throw InvalidChallenge();
        }

        var parameters = new List<WebAuthnPublicKeyCredentialParameter>(values.GetArrayLength());
        var seen = new HashSet<(string Type, int Alg)>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw InvalidChallenge();
            var type = RequiredString(value, "type", 32);
            if (!string.Equals(type, "public-key", StringComparison.Ordinal))
                throw InvalidChallenge();
            if (!TryGetProperty(value, "alg", out var algorithm)
                || !algorithm.TryGetInt32(out var alg)
                || !seen.Add((type, alg)))
            {
                throw InvalidChallenge();
            }
            parameters.Add(new WebAuthnPublicKeyCredentialParameter(type, alg));
        }
        return parameters.AsReadOnly();
    }

    private static IReadOnlyList<WebAuthnCredentialDescriptor> ParseExcludeCredentials(
        JsonElement options)
    {
        if (!TryGetProperty(options, "excludeCredentials", out var values)
            || values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<WebAuthnCredentialDescriptor>();
        }
        if (values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() > MaxCredentialCount)
        {
            throw InvalidChallenge();
        }

        var credentials = new List<WebAuthnCredentialDescriptor>(values.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw InvalidChallenge();
            var type = RequiredString(value, "type", 32);
            if (!string.Equals(type, "public-key", StringComparison.Ordinal))
                throw InvalidChallenge();

            var id = DecodeBase64Url(
                RequiredString(value, "id", 8192),
                minimumBytes: 1,
                maximumBytes: MaxCredentialIdBytes);
            var canonicalId = WebAuthnCrypto.EncodeBase64Url(id);
            if (!seen.Add(canonicalId))
                throw InvalidChallenge();

            credentials.Add(new WebAuthnCredentialDescriptor(
                type,
                id,
                ParseTransports(value)));
        }
        return credentials.AsReadOnly();
    }

    private static IReadOnlyList<string> ParseTransports(JsonElement descriptor)
    {
        if (!TryGetProperty(descriptor, "transports", out var values)
            || values.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Array.Empty<string>();
        }
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 8)
            throw InvalidChallenge();

        var transports = new List<string>(values.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateArray())
        {
            var transport = RequiredString(value, 32).ToLowerInvariant();
            if (transport is not ("usb" or "nfc" or "ble" or "internal" or "hybrid" or "smart-card")
                || !seen.Add(transport))
            {
                throw InvalidChallenge();
            }
            transports.Add(transport);
        }
        return transports.AsReadOnly();
    }

    private static int ParseTimeout(JsonElement options)
    {
        if (!TryGetProperty(options, "timeout", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 60_000;
        }
        if (!value.TryGetInt32(out var timeout) || timeout <= 0)
            throw InvalidChallenge();
        return Math.Clamp(timeout, 15_000, 120_000);
    }

    private static AuthenticatorSelection ParseAuthenticatorSelection(JsonElement options)
    {
        if (!TryGetProperty(options, "authenticatorSelection", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new AuthenticatorSelection(null, "discouraged", false, "preferred");
        }
        if (value.ValueKind != JsonValueKind.Object)
            throw InvalidChallenge();

        string? attachment = null;
        if (TryGetProperty(value, "authenticatorAttachment", out var attachmentElement)
            && attachmentElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            attachment = RequiredString(attachmentElement, 32);
            if (attachment is not ("platform" or "cross-platform"))
                throw InvalidChallenge();
        }

        var requireResidentKey = false;
        if (TryGetProperty(value, "requireResidentKey", out var requireElement)
            && requireElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (requireElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw InvalidChallenge();
            requireResidentKey = requireElement.GetBoolean();
        }

        var residentKey = requireResidentKey ? "required" : "discouraged";
        if (TryGetProperty(value, "residentKey", out var residentElement)
            && residentElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            residentKey = RequiredString(residentElement, 32);
            if (residentKey is not ("discouraged" or "preferred" or "required"))
                throw InvalidChallenge();
        }
        if (requireResidentKey && residentKey != "required")
            throw InvalidChallenge();

        var userVerification = "preferred";
        if (TryGetProperty(value, "userVerification", out var verificationElement)
            && verificationElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            userVerification = RequiredString(verificationElement, 32);
            if (userVerification is not ("discouraged" or "preferred" or "required"))
                throw InvalidChallenge();
        }

        return new AuthenticatorSelection(
            attachment,
            residentKey,
            requireResidentKey,
            userVerification);
    }

    private static string ParseAttestation(JsonElement options)
    {
        if (!TryGetProperty(options, "attestation", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "none";
        }

        var attestation = RequiredString(value, 32);
        return attestation is "none" or "indirect" or "direct" or "enterprise"
            ? attestation
            : throw InvalidChallenge();
    }

    private static void RejectUnsupportedExtensions(JsonElement options)
    {
        if (!TryGetProperty(options, "extensions", out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any())
            throw new WebAuthnTwoFactorException(
                "The WebAuthn registration challenge requires unsupported extensions.");
    }

    private static void ValidateServerStatus(JsonElement options)
    {
        if (!TryGetProperty(options, "status", out var status)
            || status.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }
        if (status.ValueKind != JsonValueKind.String
            || !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidChallenge();
        }
    }

    private static JsonElement RequiredObject(JsonElement owner, string propertyName)
    {
        if (!TryGetProperty(owner, propertyName, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw InvalidChallenge();
        }
        return value;
    }

    private static string RequiredString(JsonElement owner, string propertyName, int maxLength)
    {
        if (!TryGetProperty(owner, propertyName, out var value))
            throw InvalidChallenge();
        return RequiredString(value, maxLength);
    }

    private static string RequiredString(JsonElement value, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw InvalidChallenge();
        var result = value.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maxLength)
            throw InvalidChallenge();
        return result;
    }

    private static byte[] DecodeBase64Url(
        string value,
        int minimumBytes,
        int maximumBytes)
    {
        var paddingStart = value.IndexOf('=');
        var contentLength = paddingStart < 0 ? value.Length : paddingStart;
        if (value.Length - contentLength > 2
            || value.AsSpan(contentLength).IndexOfAnyExcept('=') >= 0
            || ContainsNonBase64Url(value.AsSpan(0, contentLength)))
        {
            throw InvalidChallenge();
        }

        var result = WebAuthnCrypto.DecodeBase64Url(value);
        if (result.Length < minimumBytes || result.Length > maximumBytes)
            throw InvalidChallenge();
        return result;
    }

    private static string CanonicalChallenge(string value) =>
        WebAuthnCrypto.EncodeBase64Url(DecodeBase64Url(value, 16, 1024));

    private static bool ContainsNonBase64Url(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                return true;
        }
        return false;
    }

    private static string NormalizeHost(string value)
    {
        var candidate = value.Trim().TrimEnd('.');
        if (IPAddress.TryParse(candidate, out var address))
            return address.ToString().ToLowerInvariant();
        return new IdnMapping().GetAscii(candidate).ToLowerInvariant();
    }

    private static bool IsSecureWebAuthnOrigin(Uri uri)
    {
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static WebAuthnTwoFactorException InvalidChallenge() =>
        new("The WebAuthn registration challenge is invalid.");

    private sealed record AuthenticatorSelection(
        string? AuthenticatorAttachment,
        string ResidentKey,
        bool RequireResidentKey,
        string UserVerification);
}
