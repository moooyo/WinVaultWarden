using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Core.Models;
using Core.Passkeys;

namespace Core.Services;

public sealed record WebAuthnTwoFactorChallenge(
    string Challenge,
    string RpId,
    IReadOnlyList<byte[]> AllowCredentialIds,
    string UserVerification,
    int TimeoutMilliseconds,
    string Origin,
    string? U2fAppId);

public sealed record WebAuthnTwoFactorAssertion(
    byte[] CredentialId,
    byte[] AuthenticatorData,
    byte[] ClientDataJson,
    byte[] Signature,
    byte[]? UserHandle,
    bool? U2fAppIdUsed);

public static class WebAuthnTwoFactorProtocol
{
    private const int MaxProviderDataBytes = 256 * 1024;
    private const int MaxCredentialCount = 64;
    private const int MaxCredentialIdBytes = 4096;
    private const int MaxAssertionPartBytes = 1024 * 1024;

    public static WebAuthnTwoFactorChallenge ParseChallenge(
        string providerDataJson,
        ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDataJson);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (Encoding.UTF8.GetByteCount(providerDataJson) > MaxProviderDataBytes)
            throw InvalidChallenge();

        try
        {
            using var document = JsonDocument.Parse(providerDataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidChallenge();

            var challengeValue = RequiredString(root, "challenge", 4096);
            var challengeBytes = DecodeBase64Url(challengeValue, 16, 1024);
            var challenge = WebAuthnCrypto.EncodeBase64Url(challengeBytes);
            var rpId = NormalizeHost(RequiredString(root, "rpId", 253));
            var originUri = new Uri(endpoints.WebVault.Origin.Value, UriKind.Absolute);
            if (!IsSecureWebAuthnOrigin(originUri)
                || !string.Equals(rpId, NormalizeHost(originUri.IdnHost), StringComparison.Ordinal))
            {
                throw InvalidChallenge();
            }

            var timeout = 60_000;
            if (root.TryGetProperty("timeout", out var timeoutElement))
            {
                if (!timeoutElement.TryGetInt32(out timeout))
                    throw InvalidChallenge();
                timeout = Math.Clamp(timeout, 15_000, 120_000);
            }

            var userVerification = root.TryGetProperty("userVerification", out var verificationElement)
                ? RequiredString(verificationElement, 32)
                : "discouraged";
            if (userVerification is not ("required" or "preferred" or "discouraged"))
                throw InvalidChallenge();

            var credentialIds = ParseCredentials(root);
            var appId = ParseAppId(root, originUri);
            return new WebAuthnTwoFactorChallenge(
                challenge,
                rpId,
                credentialIds,
                userVerification,
                timeout,
                originUri.GetLeftPart(UriPartial.Authority),
                appId);
        }
        catch (WebAuthnTwoFactorException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or UriFormatException or ArgumentException)
        {
            throw new WebAuthnTwoFactorException("The WebAuthn challenge is invalid.", ex);
        }
    }

    public static byte[] BuildClientDataJson(WebAuthnTwoFactorChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "webauthn.get");
            writer.WriteString("challenge", challenge.Challenge);
            writer.WriteString("origin", challenge.Origin);
            writer.WriteBoolean("crossOrigin", false);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static string SerializeToken(WebAuthnTwoFactorAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ValidateAssertionPart(assertion.CredentialId, 1, MaxCredentialIdBytes);
        ValidateAssertionPart(assertion.AuthenticatorData, 37, MaxAssertionPartBytes);
        ValidateAssertionPart(assertion.ClientDataJson, 1, MaxAssertionPartBytes);
        ValidateAssertionPart(assertion.Signature, 1, MaxAssertionPartBytes);
        if (assertion.UserHandle is not null)
            ValidateAssertionPart(assertion.UserHandle, 0, 64);

        var credentialId = WebAuthnCrypto.EncodeBase64Url(assertion.CredentialId);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", credentialId);
            writer.WriteString("rawId", credentialId);
            writer.WriteStartObject("response");
            writer.WriteString(
                "authenticatorData",
                WebAuthnCrypto.EncodeBase64Url(assertion.AuthenticatorData));
            writer.WriteString(
                "clientDataJson",
                WebAuthnCrypto.EncodeBase64Url(assertion.ClientDataJson));
            writer.WriteString("signature", WebAuthnCrypto.EncodeBase64Url(assertion.Signature));
            if (assertion.UserHandle is { Length: > 0 } userHandle)
                writer.WriteString("userHandle", WebAuthnCrypto.EncodeBase64Url(userHandle));
            else
                writer.WriteNull("userHandle");
            writer.WriteEndObject();
            writer.WriteStartObject("extensions");
            if (assertion.U2fAppIdUsed is bool appIdUsed)
                writer.WriteBoolean("appid", appIdUsed);
            writer.WriteEndObject();
            writer.WriteString("type", "public-key");
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyList<byte[]> ParseCredentials(JsonElement root)
    {
        if (!root.TryGetProperty("allowCredentials", out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() is 0 or > MaxCredentialCount)
        {
            throw InvalidChallenge();
        }

        var credentials = new List<byte[]>(list.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credential in list.EnumerateArray())
        {
            if (credential.ValueKind != JsonValueKind.Object
                || !credential.TryGetProperty("type", out var type)
                || !string.Equals(RequiredString(type, 32), "public-key", StringComparison.Ordinal))
            {
                throw InvalidChallenge();
            }

            var idValue = RequiredString(credential, "id", 8192);
            var id = DecodeBase64Url(idValue, 1, MaxCredentialIdBytes);
            var canonicalId = WebAuthnCrypto.EncodeBase64Url(id);
            if (!seen.Add(canonicalId))
                throw InvalidChallenge();
            credentials.Add(id);
        }
        return credentials;
    }

    private static string? ParseAppId(JsonElement root, Uri origin)
    {
        if (!root.TryGetProperty("extensions", out var extensions)
            || extensions.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (extensions.ValueKind != JsonValueKind.Object)
            throw InvalidChallenge();
        if (!extensions.TryGetProperty("appid", out var appIdElement)
            || appIdElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var appId = RequiredString(appIdElement, 2048);
        if (!Uri.TryCreate(appId, UriKind.Absolute, out var appIdUri)
            || !IsSecureWebAuthnOrigin(appIdUri)
            || !string.IsNullOrEmpty(appIdUri.UserInfo)
            || !string.IsNullOrEmpty(appIdUri.Query)
            || !string.IsNullOrEmpty(appIdUri.Fragment)
            || !string.Equals(
                appIdUri.GetLeftPart(UriPartial.Authority),
                origin.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidChallenge();
        }
        return appIdUri.AbsoluteUri;
    }

    private static string RequiredString(JsonElement owner, string propertyName, int maxLength)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
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

    private static byte[] DecodeBase64Url(string value, int minimumBytes, int maximumBytes)
    {
        var paddingStart = value.IndexOf('=');
        var contentLength = paddingStart < 0 ? value.Length : paddingStart;
        if (value.Length - contentLength > 2
            || value.AsSpan(contentLength).IndexOfAnyExcept('=') >= 0
            || value.AsSpan(0, contentLength).ContainsAnyExceptBase64Url())
        {
            throw InvalidChallenge();
        }

        var result = WebAuthnCrypto.DecodeBase64Url(value);
        if (result.Length < minimumBytes || result.Length > maximumBytes)
            throw InvalidChallenge();
        return result;
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

    private static void ValidateAssertionPart(byte[] value, int minimumBytes, int maximumBytes)
    {
        if (value is null || value.Length < minimumBytes || value.Length > maximumBytes)
            throw new WebAuthnTwoFactorException("The WebAuthn assertion is invalid.");
    }

    private static WebAuthnTwoFactorException InvalidChallenge() =>
        new("The WebAuthn challenge is invalid.");

    private static bool ContainsAnyExceptBase64Url(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                return true;
        }
        return false;
    }
}
