using System.Globalization;

namespace App.Services;

public readonly record struct OriginDisplayResult(string Display, bool IsSecure);

/// <summary>
/// WinUI-free helper that turns a raw WebAuthn origin string into a
/// human-readable "scheme://host[:port]" for display in the approval dialog.
/// Punycode hosts are decoded to Unicode; only https is treated as secure.
/// </summary>
public static class PasskeyOriginDisplay
{
    private static readonly IdnMapping Idn = new();

    public static OriginDisplayResult Parse(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return new OriginDisplayResult("(未知来源)", false);

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return new OriginDisplayResult(origin, false);

        var host = DecodeHost(uri.Host);
        var isSecure = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        var display = uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port}";

        return new OriginDisplayResult(display, isSecure);
    }

    private static string DecodeHost(string host)
    {
        try
        {
            return host.StartsWith("xn--", StringComparison.OrdinalIgnoreCase) || host.Contains(".xn--", StringComparison.OrdinalIgnoreCase)
                ? Idn.GetUnicode(host)
                : host;
        }
        catch (ArgumentException)
        {
            // Malformed punycode — fall back to the raw host rather than throwing.
            return host;
        }
    }
}
