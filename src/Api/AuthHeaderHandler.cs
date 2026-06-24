namespace Api;

public sealed class AuthHeaderHandler : DelegatingHandler
{
    public const string ClientName = "desktop";
    public const string ClientVersion = "2026.6.0";
    public const string DeviceType = "6";

    private readonly Func<string?> _getAccessToken;
    private readonly Func<CancellationToken, Task<bool>> _refreshAsync;

    public AuthHeaderHandler(Func<string?> getAccessToken, Func<CancellationToken, Task<bool>> refreshAsync)
    {
        _getAccessToken = getAccessToken;
        _refreshAsync = refreshAsync;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApplyHeaders(request);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        if (!await _refreshAsync(cancellationToken))
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { RequestMessage = request };

        var retry = await CloneRequestAsync(request, cancellationToken);
        ApplyHeaders(retry);
        return await base.SendAsync(retry, cancellationToken);
    }

    private void ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.Remove("Bitwarden-Client-Name");
        request.Headers.Remove("Bitwarden-Client-Version");
        request.Headers.Remove("Device-Type");
        request.Headers.Add("Bitwarden-Client-Name", ClientName);
        request.Headers.Add("Bitwarden-Client-Version", ClientVersion);
        request.Headers.Add("Device-Type", DeviceType);

        var token = _getAccessToken();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
