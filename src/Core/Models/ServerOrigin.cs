using System.Text.Json.Serialization;

namespace Core.Models;

public readonly record struct ServerOrigin
{
    private readonly string? _value;

    [JsonConstructor]
    public ServerOrigin(string value)
    {
        if (!TryNormalize(value, out var normalized))
            throw new ArgumentException("Server origin must be an absolute HTTP(S) URI without user info.", nameof(value));
        _value = normalized;
    }

    public string Value => _value
        ?? throw new InvalidOperationException("ServerOrigin is not initialized.");

    public bool IsInitialized => _value is not null;

    public bool Matches(Uri uri) =>
        uri.IsAbsoluteUri
        && TryParse(uri.GetLeftPart(UriPartial.Authority), out var candidate)
        && candidate == this;

    public Uri Resolve(string relativeOrAbsolute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOrAbsolute);

        // Reject protocol-relative ("//", "///") and backslash/UNC paths BEFORE calling
        // Uri.TryCreate, which would silently interpret them as file:// URIs.
        if (relativeOrAbsolute.StartsWith("//", StringComparison.Ordinal) ||
            relativeOrAbsolute.StartsWith("\\", StringComparison.Ordinal))
            throw new ArgumentException(
                "Protocol-relative and UNC paths are not allowed.", nameof(relativeOrAbsolute));

        if (Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absolute))
        {
            // Only http/https absolute URIs are permitted.
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException(
                    $"Absolute URI scheme '{absolute.Scheme}' is not allowed; only http/https are permitted.",
                    nameof(relativeOrAbsolute));
            // The absolute URI must belong to the same origin.
            if (!Matches(absolute))
                throw new ArgumentException(
                    "Absolute URI must belong to the same origin as this ServerOrigin.",
                    nameof(relativeOrAbsolute));
            return absolute;
        }

        return new Uri(new Uri(Value + "/", UriKind.Absolute), relativeOrAbsolute.TrimStart('/'));
    }

    public static bool TryParse(string? value, out ServerOrigin origin)
    {
        if (TryNormalize(value, out var normalized))
        {
            origin = new ServerOrigin(normalized);
            return true;
        }
        origin = default;
        return false;
    }

    public override string ToString() => Value;

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var scheme = uri.Scheme.ToLowerInvariant();
        // Trim trailing dot(s) so that FQDN "example.com." normalises to "example.com".
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(host))
            return false;
        var port = uri.IsDefaultPort ? -1 : uri.Port;
        var builder = new UriBuilder(scheme, host, port);
        normalized = builder.Uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
