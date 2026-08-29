using System.Net.Http.Headers;
using Core.Models;

namespace Api;

public sealed class ClientHeadersHandler : DelegatingHandler
{
    public const string ClientName = "desktop";
    public const string ClientVersion = "2026.6.0";
    public const string DeviceType = "6";

    private static readonly HttpRequestOptionsKey<ScopedBearerAuthorization>
        ScopedBearerAuthorizationKey = new("WinVaultWarden.ScopedBearerAuthorization");

    internal static void SetScopedBearerAuthorization(
        HttpRequestMessage request,
        ServerEndpoint audience,
        string token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        request.Options.Set(
            ScopedBearerAuthorizationKey,
            new ScopedBearerAuthorization(audience, token));
    }

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
        if (request.Options.TryGetValue(ScopedBearerAuthorizationKey, out var authorization))
        {
            if (request.RequestUri is not { IsAbsoluteUri: true } target
                || !authorization.Audience.Matches(target))
            {
                throw new InvalidOperationException(
                    "Scoped bearer authorization cannot be sent outside its bound API audience.");
            }

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", authorization.Token);
        }
        return base.SendAsync(request, ct);
    }

    private sealed record ScopedBearerAuthorization(
        ServerEndpoint Audience,
        string Token);
}
