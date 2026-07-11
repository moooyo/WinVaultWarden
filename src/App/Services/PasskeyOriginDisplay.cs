using System.Globalization;

namespace App.Services;

// Both the ASCII (A-label/punycode) and the IDN-decoded Unicode forms of a normalised origin.
// The A-label is the security-relevant, unspoofable form; the Unicode form is only a readable hint.
public readonly record struct OriginDisplayResult(string Unicode, string Ascii, bool IsSecure);

/// <summary>
/// WinUI-free helper that turns an already-normalised WebAuthn origin (the Task 1 policy's
/// A-label <c>scheme://host[:port]</c>) into paired ASCII and Unicode strings for the approval
/// dialog. Punycode hosts are decoded to Unicode for readability while the A-label is preserved
/// verbatim; only https is treated as secure. Unparseable input is collapsed to a fixed
/// placeholder — the raw value is never echoed back into the UI.
/// </summary>
public static class PasskeyOriginDisplay
{
    private const string Unknown = "(未知来源)";
    private static readonly IdnMapping Idn = new();

    public static OriginDisplayResult Parse(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return new OriginDisplayResult(Unknown, Unknown, false);
        }

        var (ascii, unicode) = Hosts(uri);
        var isSecure = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        return new OriginDisplayResult(
            Format(uri, unicode),
            Format(uri, ascii),
            isSecure);
    }

    private static string Format(Uri uri, string host) =>
        uri.IsDefaultPort
            ? $"{uri.Scheme}://{host}"
            : $"{uri.Scheme}://{host}:{uri.Port}";

    private static (string Ascii, string Unicode) Hosts(Uri uri)
    {
        // IPv6 literals keep their bracketed host form; there is no punycode to decode.
        if (uri.HostNameType == UriHostNameType.IPv6)
            return (uri.Host, uri.Host);

        var ascii = uri.IdnHost;
        return (ascii, DecodeHost(ascii));
    }

    private static string DecodeHost(string host)
    {
        try
        {
            return host.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)
                || host.Contains(".xn--", StringComparison.OrdinalIgnoreCase)
                ? Idn.GetUnicode(host)
                : host;
        }
        catch (ArgumentException)
        {
            // Malformed punycode — fall back to the A-label rather than throwing.
            return host;
        }
    }
}
