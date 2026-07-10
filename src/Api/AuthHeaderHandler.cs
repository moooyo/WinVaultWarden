using System.Net;
using System.Net.Http.Headers;
using Core.Models;
using Core.Session;

namespace Api;

public sealed class AuthHeaderHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> DisableAutomaticRetryKey =
        new("WinVaultWarden.DisableAutomaticRetry");

    private readonly ServerOrigin _clientOrigin;
    private readonly Func<AccessTokenBinding?> _tokenProvider;
    private readonly Func<CancellationToken, Task<bool>> _refreshAsync;

    public AuthHeaderHandler(
        ServerOrigin clientOrigin,
        Func<AccessTokenBinding?> tokenProvider,
        Func<CancellationToken, Task<bool>> refreshAsync)
    {
        _clientOrigin = clientOrigin;
        _tokenProvider = tokenProvider;
        _refreshAsync = refreshAsync;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var attached = ApplyHeaders(request);
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized
            || !attached
            || IsTokenEndpoint(request)
            || request.Options.TryGetValue(DisableAutomaticRetryKey, out var disabled)
               && disabled)
            return response;

        var retry = await CloneRequestAsync(request, ct).ConfigureAwait(false);
        response.Dispose();
        if (!await _refreshAsync(ct).ConfigureAwait(false))
        {
            retry.Dispose();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                RequestMessage = request,
            };
        }

        ApplyHeaders(retry);
        return await base.SendAsync(retry, ct).ConfigureAwait(false);
    }

    private bool ApplyHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = null;
        ClientHeadersHandler.Apply(request);
        var binding = _tokenProvider();
        if (request.RequestUri is not { IsAbsoluteUri: true } target
            || binding is null
            || binding.Origin != _clientOrigin
            || !_clientOrigin.Matches(target))
            return false;

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", binding.Token);
        return true;
    }

    private static bool IsTokenEndpoint(HttpRequestMessage request) =>
        request.RequestUri?.AbsolutePath.EndsWith(
            "/identity/connect/token",
            StringComparison.OrdinalIgnoreCase) == true;

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }
}
