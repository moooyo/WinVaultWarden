namespace Api;

public sealed class ClientHeadersHandler : DelegatingHandler
{
    public const string ClientName = "desktop";
    public const string ClientVersion = "2026.6.0";
    public const string DeviceType = "6";

    internal static void Apply(HttpRequestMessage request)
    {
        request.Headers.Remove("Bitwarden-Client-Name");
        request.Headers.Remove("Bitwarden-Client-Version");
        request.Headers.Remove("Device-Type");
        request.Headers.TryAddWithoutValidation(
            "Bitwarden-Client-Name", ClientName);
        request.Headers.TryAddWithoutValidation(
            "Bitwarden-Client-Version", ClientVersion);
        request.Headers.TryAddWithoutValidation(
            "Device-Type", DeviceType);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        request.Headers.Authorization = null;
        Apply(request);
        return base.SendAsync(request, ct);
    }
}
