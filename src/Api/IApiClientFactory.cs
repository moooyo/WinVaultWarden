using Core.Models;
using Core.Session;

namespace Api;

public interface IAnonymousApiRole { }
public interface IAuthenticatedApiRole { }

public interface IApiClientFactory
{
    TClient CreateAnonymous<TClient>(ServerEndpoints endpoints)
        where TClient : class, IAnonymousApiRole;

    TClient CreateAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole;

    IAuthenticatedApiClient CreateAuthenticated(
        ServerEndpoints endpoints,
        Func<AccessTokenBinding?> tokenProvider,
        Func<AccessTokenBinding, CancellationToken, Task<bool>> refreshAsync);

    IAuthenticatedApiClient CreateAuthenticated(
        ServerOrigin origin,
        Func<AccessTokenBinding?> tokenProvider,
        Func<CancellationToken, Task<bool>> refreshAsync);
}
