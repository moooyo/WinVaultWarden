namespace Core;

// 从登录条目 URI 提取用于 favicon 请求的 host。纯逻辑、无依赖。
public static class IconDomain
{
    public static string? Extract(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        var candidate = uri.Contains("://", StringComparison.Ordinal) ? uri : "https://" + uri;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            return null;

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return null;

        var host = parsed.Host;
        return string.IsNullOrEmpty(host) ? null : host.ToLowerInvariant();
    }
}
