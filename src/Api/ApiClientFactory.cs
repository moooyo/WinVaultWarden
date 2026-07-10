using Core.Models;
using Core.Session;

namespace Api;

public sealed class ApiClientFactory : IApiClientFactory
{
    private readonly Func<bool, HttpMessageHandler> _transportFactory;

    public ApiClientFactory() : this(authenticated => new HttpClientHandler
    {
        AllowAutoRedirect = !authenticated,
    })
    {
    }

    public ApiClientFactory(Func<bool, HttpMessageHandler> transportFactory) =>
        _transportFactory = transportFactory;

    public TClient CreateAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole
    {
        var headers = new ClientHeadersHandler
        {
            InnerHandler = _transportFactory(false),
        };
        return Cast<TClient>(
            new ApiClient(new HttpClient(headers, disposeHandler: true), origin));
    }

    public IAuthenticatedApiClient CreateAuthenticated(
        ServerOrigin origin,
        Func<AccessTokenBinding?> tokenProvider,
        Func<CancellationToken, Task<bool>> refreshAsync)
    {
        var auth = new AuthHeaderHandler(origin, tokenProvider, refreshAsync)
        {
            InnerHandler = _transportFactory(true),
        };
        return new ApiClient(new HttpClient(auth, disposeHandler: true), origin);
    }

    private static TClient Cast<TClient>(ApiClient client)
        where TClient : class =>
        client as TClient
        ?? throw new NotSupportedException(
            $"Unsupported API role: {typeof(TClient).FullName}");
}
