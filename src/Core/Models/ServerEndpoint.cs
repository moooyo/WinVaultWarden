using System.Text.Json.Serialization;

namespace Core.Models;

/// <summary>
/// Identifies an HTTP(S) service endpoint, including its path prefix.
/// </summary>
public readonly record struct ServerEndpoint
{
    private readonly string? _value;

    [JsonConstructor]
    public ServerEndpoint(string value)
    {
        if (!TryNormalize(value, out var normalized))
        {
            throw new ArgumentException(
                "Server endpoint must be an absolute HTTP(S) URI without user info, query, or fragment.",
                nameof(value));
        }

        _value = normalized;
    }

    public string Value => _value
        ?? throw new InvalidOperationException("ServerEndpoint is not initialized.");

    public bool IsInitialized => _value is not null;

    public ServerOrigin Origin => new(Value);

    /// <summary>
    /// Gets the normalized escaped path prefix, or an empty string for an authority-root endpoint.
    /// </summary>
    public string BasePath
    {
        get
        {
            var path = new Uri(Value, UriKind.Absolute).AbsolutePath;
            return path == "/" ? string.Empty : path;
        }
    }

    /// <summary>
    /// Returns whether an absolute URI belongs to this endpoint's origin and path-prefix boundary.
    /// </summary>
    public bool Matches(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri
            || HasUnsafeRawPathSegment(uri.OriginalString)
            || !Origin.Matches(uri))
            return false;

        var basePath = BasePath;
        if (basePath.Length == 0)
            return !ContainsEscapedPathSeparator(uri.AbsolutePath);

        var candidatePath = NormalizePathForComparison(uri.AbsolutePath);
        if (ContainsEscapedPathSeparator(candidatePath))
            return false;

        return candidatePath.Equals(basePath, StringComparison.Ordinal)
            || (candidatePath.Length > basePath.Length
                && candidatePath.StartsWith(basePath, StringComparison.Ordinal)
                && candidatePath[basePath.Length] == '/');
    }

    /// <summary>
    /// Resolves a request URI while preventing origin changes and path-prefix escapes.
    /// </summary>
    public Uri Resolve(string relativeOrAbsolute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOrAbsolute);

        var value = relativeOrAbsolute.Trim();
        ValidateReferenceSyntax(value, nameof(relativeOrAbsolute));

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (!IsHttpScheme(absolute.Scheme))
            {
                throw new ArgumentException(
                    $"Absolute URI scheme '{absolute.Scheme}' is not allowed; only http/https are permitted.",
                    nameof(relativeOrAbsolute));
            }

            if (!Matches(absolute))
            {
                throw new ArgumentException(
                    "Absolute URI must belong to this ServerEndpoint.",
                    nameof(relativeOrAbsolute));
            }

            return absolute;
        }

        if (value.StartsWith('/'))
        {
            throw new ArgumentException(
                "A relative endpoint path must not be rooted.",
                nameof(relativeOrAbsolute));
        }

        Uri resolved;
        try
        {
            resolved = value.StartsWith('?')
                ? new Uri(Value + value, UriKind.Absolute)
                : new Uri(new Uri(Value + "/", UriKind.Absolute), value);
        }
        catch (UriFormatException exception)
        {
            throw new ArgumentException("The endpoint path is not a valid URI reference.", nameof(relativeOrAbsolute), exception);
        }

        if (!Matches(resolved))
        {
            throw new ArgumentException(
                "The relative URI must not escape this ServerEndpoint's path prefix.",
                nameof(relativeOrAbsolute));
        }

        return resolved;
    }

    /// <summary>
    /// Creates a child endpoint beneath this endpoint's path prefix.
    /// </summary>
    public ServerEndpoint Append(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var value = relativePath.Trim();
        if (value.Contains('?') || value.Contains('#'))
        {
            throw new ArgumentException(
                "An appended endpoint path must not contain a query or fragment.",
                nameof(relativePath));
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            throw new ArgumentException("An appended endpoint path must be relative.", nameof(relativePath));
        }

        Uri resolved;
        try
        {
            resolved = Resolve(value);
        }
        catch (ArgumentException exception) when (exception.ParamName == "relativeOrAbsolute")
        {
            throw new ArgumentException(exception.Message, nameof(relativePath), exception);
        }

        var child = new ServerEndpoint(resolved.GetLeftPart(UriPartial.Path));
        if (child == this)
        {
            throw new ArgumentException(
                "An appended endpoint path must identify a child endpoint.",
                nameof(relativePath));
        }

        return child;
    }

    public static bool TryParse(string? value, out ServerEndpoint endpoint)
    {
        if (TryNormalize(value, out var normalized))
        {
            endpoint = new ServerEndpoint(normalized);
            return true;
        }

        endpoint = default;
        return false;
    }

    public override string ToString() => Value;

    private static void ValidateReferenceSyntax(string value, string parameterName)
    {
        if (value.Contains('\\'))
        {
            throw new ArgumentException("Backslash paths are not allowed.", parameterName);
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException("Protocol-relative paths are not allowed.", parameterName);
        }

        if (value.Contains('#'))
        {
            throw new ArgumentException("URI fragments are not allowed.", parameterName);
        }

        if (HasUnsafeRawPathSegment(value))
        {
            throw new ArgumentException(
                "URI paths must not contain dot segments or encoded path separators.",
                parameterName);
        }

        if (HasRfcSchemePrefixWithoutAuthority(value))
        {
            throw new ArgumentException(
                "RFC scheme prefixes must use '//' before the authority component.",
                parameterName);
        }
    }

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var candidate = value.Trim();
        if (candidate.Contains('\\')
            || candidate.Contains('?')
            || candidate.Contains('#')
            || HasUnsafeRawPathSegment(candidate)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !IsHttpScheme(uri.Scheme)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var path = NormalizePathForComparison(uri.AbsolutePath);
        if (ContainsEscapedPathSeparator(path))
            return false;

        ServerOrigin origin;
        try
        {
            origin = new ServerOrigin(candidate);
        }
        catch (ArgumentException)
        {
            return false;
        }

        normalized = path.Length == 0 ? origin.Value : origin.Value + path;
        return true;
    }

    private static string NormalizePathForComparison(string path)
    {
        if (path.Length == 0 || path == "/")
            return string.Empty;

        return path.TrimEnd('/');
    }

    private static bool ContainsEscapedPathSeparator(string path) =>
        path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || path.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    private static bool HasUnsafeRawPathSegment(string value)
    {
        var queryIndex = value.IndexOf('?');
        var rawPathAndAuthority = queryIndex >= 0 ? value[..queryIndex] : value;
        var authorityMarker = rawPathAndAuthority.IndexOf("://", StringComparison.Ordinal);
        var rawPath = rawPathAndAuthority;

        if (authorityMarker >= 0)
        {
            var authorityStart = authorityMarker + 3;
            var pathStart = rawPathAndAuthority.IndexOf('/', authorityStart);
            rawPath = pathStart >= 0 ? rawPathAndAuthority[pathStart..] : string.Empty;
        }

        foreach (var segment in rawPath.Split('/', StringSplitOptions.None))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\'))
                return true;
        }

        return false;
    }

    private static bool IsHttpScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool HasRfcSchemePrefixWithoutAuthority(string value)
    {
        var colonIndex = value.IndexOf(':');
        if (colonIndex <= 0 || !char.IsLetter(value[0]))
            return false;

        for (var i = 1; i < colonIndex; i++)
        {
            var character = value[i];
            if (!char.IsLetterOrDigit(character) && character != '+' && character != '-' && character != '.')
                return false;
        }

        return colonIndex + 2 >= value.Length
            || value[colonIndex + 1] != '/'
            || value[colonIndex + 2] != '/';
    }
}
