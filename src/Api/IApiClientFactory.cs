using Core.Models;
using Core.Session;

namespace Api;

public interface IAnonymousApiRole { }
public interface IAuthenticatedApiRole { }

public interface IApiClientFactory
{
    TClient CreateAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole;

    IAuthenticatedApiClient CreateAuthenticated(
        ServerOrigin origin,
        Func<AccessTokenBinding?> tokenProvider,
        Func<CancellationToken, Task<bool>> refreshAsync);
}
