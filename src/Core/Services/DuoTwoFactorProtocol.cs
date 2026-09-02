using System.Net;
using System.Text;
using System.Text.Json;
using Core.Models;

namespace Core.Services;

public sealed record DuoTwoFactorChallenge(
    Uri AuthorizationUri,
    Uri CallbackUri,
    string State);

public static class DuoTwoFactorProtocol
{
    private const int MaxProviderDataBytes = 256 * 1024;
    private const int MaxJwtBytes = 64 * 1024;

    public static DuoTwoFactorChallenge ParseChallenge(
        string providerDataJson,
        ServerEndpoints endpoints,
        DateTimeOffset? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDataJson);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (Encoding.UTF8.GetByteCount(providerDataJson) > MaxProviderDataBytes)
            throw InvalidChallenge();

        try
        {
            using var provider = JsonDocument.Parse(providerDataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            var root = provider.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidChallenge();
            var authUrl = OptionalString(root, "AuthUrl", "authUrl");
            var host = OptionalString(root, "Host", "host");
            var signature = OptionalString(root, "Signature", "signature");
            if (authUrl is not null && signature is not null)
                throw InvalidChallenge();
            if (authUrl is null)
            {
                if (host is not null
                    && signature is not null
                    && IsAllowedDuoHost(host)
                    && IsLegacySignature(signature))
                {
                    throw new DuoLegacyUnsupportedException();
                }

                throw InvalidChallenge();
            }

            if (!Uri.TryCreate(authUrl, UriKind.Absolute, out var authorizationUri)
                || !IsAllowedDuoAuthorizationUri(authorizationUri)
                || (host is not null
                    && !string.Equals(host, authorizationUri.IdnHost, StringComparison.OrdinalIgnoreCase))
                || !TryParseUniqueQuery(authorizationUri.Query, out var query)
                || !query.TryGetValue("response_type", out var responseType)
                || responseType != "code"
                || !query.TryGetValue("client_id", out var clientId)
                || string.IsNullOrWhiteSpace(clientId)
                || clientId.Length > 256
                || !query.TryGetValue("request", out var requestJwt))
            {
                throw InvalidChallenge();
            }

            var callback = BuildCallbackUri(endpoints);
            var state = ValidateRequestJwt(
                requestJwt,
                authorizationUri,
                callback,
                clientId,
                utcNow ?? DateTimeOffset.UtcNow);
            return new DuoTwoFactorChallenge(authorizationUri, callback, state);
        }
        catch (DuoTwoFactorException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or UriFormatException or ArgumentException)
        {
            throw new DuoTwoFactorException("The Duo challenge is invalid.", ex);
        }
    }

    public static bool TryCreateToken(
        DuoTwoFactorChallenge challenge,
        Uri navigationUri,
        out string token)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(navigationUri);
        token = string.Empty;
        if (!SameOrigin(challenge.CallbackUri, navigationUri)
            || !string.IsNullOrEmpty(navigationUri.UserInfo)
            || !string.IsNullOrEmpty(navigationUri.Fragment)
            || !string.Equals(
                challenge.CallbackUri.AbsolutePath,
                navigationUri.AbsolutePath,
                StringComparison.Ordinal)
            || !TryParseUniqueQuery(navigationUri.Query, out var query)
            || query.Count != 3
            || !query.TryGetValue("client", out var client)
            || client != "desktop"
            || !query.TryGetValue("code", out var code)
            || string.IsNullOrWhiteSpace(code)
            || code.Length > 4096
            || code.Contains('|')
            || !query.TryGetValue("state", out var state)
            || !string.Equals(state, challenge.State, StringComparison.Ordinal))
        {
            return false;
        }

        token = $"{code}|{state}";
        return true;
    }

    private static string ValidateRequestJwt(
        string requestJwt,
        Uri authorizationUri,
        Uri expectedCallback,
        string queryClientId,
        DateTimeOffset utcNow)
    {
        if (Encoding.ASCII.GetByteCount(requestJwt) > MaxJwtBytes)
            throw InvalidChallenge();
        var parts = requestJwt.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            throw InvalidChallenge();

        using var header = JsonDocument.Parse(DecodeBase64Url(parts[0]));
        if (header.RootElement.ValueKind != JsonValueKind.Object
            || TryString(header.RootElement, "alg") != "HS512")
        {
            throw InvalidChallenge();
        }

        using var payload = JsonDocument.Parse(DecodeBase64Url(parts[1]));
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || TryString(root, "response_type") != "code"
            || TryString(root, "scope") != "openid")
        {
            throw InvalidChallenge();
        }

        var clientId = RequiredString(root, "client_id", 256);
        var issuer = RequiredString(root, "iss", 256);
        if (!string.Equals(clientId, queryClientId, StringComparison.Ordinal)
            || !string.Equals(issuer, clientId, StringComparison.Ordinal))
        {
            throw InvalidChallenge();
        }

        var audience = new Uri(RequiredString(root, "aud", 2048), UriKind.Absolute);
        if (!SameOrigin(audience, authorizationUri))
            throw InvalidChallenge();

        var redirect = new Uri(RequiredString(root, "redirect_uri", 2048), UriKind.Absolute);
        if (!SameOrigin(redirect, expectedCallback)
            || !string.Equals(redirect.AbsolutePath, expectedCallback.AbsolutePath, StringComparison.Ordinal)
            || !TryParseUniqueQuery(redirect.Query, out var redirectQuery)
            || redirectQuery.Count != 1
            || !redirectQuery.TryGetValue("client", out var redirectClient)
            || redirectClient != "desktop")
        {
            throw InvalidChallenge();
        }

        if (!root.TryGetProperty("exp", out var expirationElement)
            || !expirationElement.TryGetInt64(out var expiration))
            throw InvalidChallenge();
        var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expiration);
        if (expirationTime < utcNow.AddSeconds(-30)
            || expirationTime > utcNow.AddMinutes(10))
        {
            throw InvalidChallenge();
        }

        var state = RequiredString(root, "state", 1024);
        _ = RequiredString(root, "nonce", 1024);
        _ = RequiredString(root, "duo_uname", 320);
        if (state.Length < 16
            || state.Contains('|')
            || state.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw InvalidChallenge();
        }
        return state;
    }

    private static Uri BuildCallbackUri(ServerEndpoints endpoints) =>
        new($"{endpoints.WebVault.Value.TrimEnd('/')}/duo-redirect-connector.html?client=desktop");

    private static bool IsAllowedDuoAuthorizationUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsDefaultPort
            || uri.AbsolutePath != "/oauth/v1/authorize")
        {
            return false;
        }

        return IsAllowedDuoHost(uri.IdnHost);
    }

    private static bool IsAllowedDuoHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)
            || host.Length > 253
            || host.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.'))
        {
            return false;
        }

        host = host.ToLowerInvariant();
        const string commercialSuffix = ".duosecurity.com";
        const string federalSuffix = ".duofederal.com";
        var suffix = host.EndsWith(commercialSuffix, StringComparison.Ordinal)
            ? commercialSuffix
            : host.EndsWith(federalSuffix, StringComparison.Ordinal)
                ? federalSuffix
                : null;
        if (suffix is null)
            return false;
        var prefix = host[..^suffix.Length];
        return prefix.StartsWith("api-", StringComparison.Ordinal)
            && prefix.Length > 4
            && prefix[4..].All(char.IsAsciiLetterOrDigit);
    }

    private static bool IsLegacySignature(string signature)
    {
        if (string.IsNullOrWhiteSpace(signature) || signature.Length > 32 * 1024)
            return false;

        var parts = signature.Split(':');
        return parts.Length == 2
            && IsLegacySignaturePart(parts[0], "TX")
            && IsLegacySignaturePart(parts[1], "APP");
    }

    private static bool IsLegacySignaturePart(string value, string expectedPrefix)
    {
        var fields = value.Split('|');
        return fields.Length == 3
            && fields[0] == expectedPrefix
            && fields[1].Length is > 0 and <= 16 * 1024
            && IsLegacyBase64(fields[1])
            && fields[2].Length == 40
            && fields[2].All(character =>
                char.IsAsciiDigit(character)
                || character is >= 'a' and <= 'f'
                || character is >= 'A' and <= 'F');
    }

    private static bool IsLegacyBase64(string value)
    {
        var paddingStart = value.IndexOf('=');
        var contentLength = paddingStart < 0 ? value.Length : paddingStart;
        if (contentLength == 0
            || value.Length - contentLength > 2
            || value.AsSpan(contentLength).IndexOfAnyExcept('=') >= 0)
        {
            return false;
        }

        foreach (var character in value.AsSpan(0, contentLength))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '+' and not '/')
                return false;
        }
        return true;
    }

    private static bool TryParseUniqueQuery(string rawQuery, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = WebUtility.UrlDecode(parts[0]);
            var value = WebUtility.UrlDecode(parts.Length == 2 ? parts[1] : string.Empty);
            if (string.IsNullOrEmpty(key) || !values.TryAdd(key, value))
                return false;
        }
        return true;
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var paddingStart = value.IndexOf('=');
        var contentLength = paddingStart < 0 ? value.Length : paddingStart;
        if (value.Length - contentLength > 2
            || value.AsSpan(contentLength).IndexOfAnyExcept('=') >= 0)
        {
            throw InvalidChallenge();
        }
        foreach (var character in value.AsSpan(0, contentLength))
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
                throw InvalidChallenge();
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        var result = Convert.FromBase64String(base64);
        if (result.Length is 0 or > MaxJwtBytes)
            throw InvalidChallenge();
        return result;
    }

    private static string RequiredString(JsonElement owner, string propertyName, int maximumLength) =>
        TryString(owner, propertyName) is { } value
            && !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
                ? value
                : throw InvalidChallenge();

    private static string? TryString(JsonElement owner, string propertyName) =>
        owner.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? OptionalString(
        JsonElement owner,
        string primaryName,
        string compatibilityName)
    {
        JsonElement? found = null;
        foreach (var property in owner.EnumerateObject())
        {
            if (property.Name != primaryName && property.Name != compatibilityName)
                continue;
            if (found is not null)
                throw InvalidChallenge();
            found = property.Value;
        }

        return found is null
            ? null
            : found.Value.ValueKind == JsonValueKind.String
                ? found.Value.GetString() ?? throw InvalidChallenge()
                : throw InvalidChallenge();
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static DuoTwoFactorException InvalidChallenge() =>
        new("The Duo challenge is invalid.");
}
