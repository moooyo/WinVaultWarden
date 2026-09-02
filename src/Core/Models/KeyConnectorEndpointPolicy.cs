namespace Core.Models;

public static class KeyConnectorEndpointPolicy
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || !ServerEndpoint.TryParse(value, out var endpoint))
        {
            return false;
        }

        var uri = new Uri(endpoint.Value, UriKind.Absolute);
        var secure = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var loopback = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (System.Net.IPAddress.TryParse(uri.Host, out var address)
                    && System.Net.IPAddress.IsLoopback(address)));
        if (!secure && !loopback)
            return false;

        normalized = endpoint.Value;
        return true;
    }

    public static Uri ResolveUserKeys(string baseUrl)
    {
        if (!TryNormalize(baseUrl, out var normalized))
            throw new ArgumentException("The Key Connector URL is invalid.", nameof(baseUrl));
        return new Uri(
            new ServerEndpoint(normalized).Append("user-keys").Value,
            UriKind.Absolute);
    }
}
