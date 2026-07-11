using System.Globalization;
using System.Net;
using System.Text;
using Core.Models;

namespace Core.Passkeys;

// Decides whether a browser passkey request may proceed, based solely on the trusted
// BrowserRequestContext. Order: absolute origin with no user-info/query/fragment/non-root
// path; HTTPS or one of the three exact loopback HTTP hosts; ServerOrigin normalisation;
// IDN NFC + A-label round-trip; exact IP RP match; public-suffix rejection; label-boundary
// RP ownership; create top-level only; cross-origin get Permissions Policy. Denials return
// a stable code with no input echoed back.
public sealed class WebAuthnRequestPolicy
{
    private readonly PublicSuffixList _suffixes;

    public WebAuthnRequestPolicy(PublicSuffixList suffixes) =>
        _suffixes = suffixes;

    public static WebAuthnRequestPolicy CreateDefault() =>
        new(PublicSuffixList.LoadDefault());

    public WebAuthnPolicyDecision Evaluate(
        BrowserPasskeyOperation operation,
        BrowserRequestContext context,
        string? requestedRpId)
    {
        if (!TrySecureOrigin(context.FrameOrigin, out var frame, out var frameUri)
            || !TrySecureOrigin(context.TopOrigin, out var top, out var topUri))
            return Deny(PasskeyErrorCodes.SecurityInsecureOrigin);

        if (!TryCanonicalHost(frameUri.IdnHost, out var frameHost, out _)
            || !TryCanonicalHost(topUri.IdnHost, out _, out _))
            return Deny(PasskeyErrorCodes.SecurityIdn);

        var requested = string.IsNullOrWhiteSpace(requestedRpId) ? frameHost : requestedRpId;
        if (!TryCanonicalRp(requested!, out var rpAscii, out var rpUnicode))
            return Deny(PasskeyErrorCodes.SecurityIdn);

        var frameIsIp = IPAddress.TryParse(frameHost, out _);
        if (frameIsIp
            ? !string.Equals(frameHost, rpAscii, StringComparison.Ordinal)
            : !IsSameOrSubdomain(frameHost, rpAscii))
            return Deny(PasskeyErrorCodes.SecurityInvalidRp);

        if (!frameIsIp && rpAscii != "localhost" && _suffixes.IsPublicSuffix(rpAscii))
            return Deny(PasskeyErrorCodes.SecurityPublicSuffix);

        var crossOrigin = frame != top;
        if (operation == BrowserPasskeyOperation.Create
            && (context.FrameId != 0 || crossOrigin))
            return Deny(PasskeyErrorCodes.SecurityFrame);
        if (operation == BrowserPasskeyOperation.Get
            && crossOrigin
            && !context.PublicKeyCredentialsGetAllowed)
            return Deny(PasskeyErrorCodes.SecurityPermissionsPolicy);

        return new(new(frame, top, rpAscii, rpUnicode, crossOrigin), null);
    }

    private static bool TrySecureOrigin(
        string value,
        out ServerOrigin origin,
        out Uri uri)
    {
        origin = default;
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.UserInfo.Length != 0
            || parsed.Query.Length != 0
            || parsed.Fragment.Length != 0
            || parsed.AbsolutePath != "/")
            return false;

        var host = parsed.IdnHost.Trim('[', ']');
        var https = parsed.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        var exactLoopback = parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1"
                || host == "::1");
        if (!https && !exactLoopback)
            return false;

        if (!ServerOrigin.TryParse(parsed.GetLeftPart(UriPartial.Authority), out origin))
            return false;
        uri = new Uri(origin.Value, UriKind.Absolute);
        return true;
    }

    private static bool TryCanonicalHost(
        string value,
        out string ascii,
        out string unicode)
    {
        ascii = string.Empty;
        unicode = string.Empty;
        var candidate = value.Trim('[', ']');
        if (IPAddress.TryParse(candidate, out var address))
        {
            ascii = address.ToString().ToLowerInvariant();
            unicode = ascii;
            return true;
        }
        return TryCanonicalRp(candidate, out ascii, out unicode);
    }

    private static bool TryCanonicalRp(
        string value,
        out string ascii,
        out string unicode)
    {
        ascii = string.Empty;
        unicode = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.EndsWith('.')
            || value != value.Normalize(NormalizationForm.FormC))
            return false;
        try
        {
            if (IPAddress.TryParse(value.Trim('[', ']'), out var address))
            {
                ascii = address.ToString().ToLowerInvariant();
                unicode = ascii;
                return value.Trim('[', ']').Equals(ascii, StringComparison.Ordinal);
            }

            var idn = new IdnMapping { UseStd3AsciiRules = true };
            ascii = string.Join('.', value.Split('.').Select(
                label => idn.GetAscii(label).ToLowerInvariant()));
            unicode = string.Join('.', ascii.Split('.').Select(
                label => idn.GetUnicode(label)))
                .Normalize(NormalizationForm.FormC);
            var roundTrip = string.Join('.', unicode.Split('.').Select(
                label => idn.GetAscii(label).ToLowerInvariant()));
            if (!roundTrip.Equals(ascii, StringComparison.Ordinal))
                return false;
            if (value.Contains("xn--", StringComparison.OrdinalIgnoreCase)
                && !value.Equals(ascii, StringComparison.Ordinal))
                return false;
            return ascii.Split('.').All(label => label.Length is > 0 and <= 63);
        }
        catch (ArgumentException)
        {
            ascii = string.Empty;
            unicode = string.Empty;
            return false;
        }
    }

    private static bool IsSameOrSubdomain(string host, string rpId) =>
        host.Equals(rpId, StringComparison.Ordinal)
        || host.EndsWith("." + rpId, StringComparison.Ordinal);

    private static WebAuthnPolicyDecision Deny(string code) => new(null, code);
}
