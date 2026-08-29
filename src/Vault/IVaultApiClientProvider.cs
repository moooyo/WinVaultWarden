using Api;
using Core.Models;
using Core.Session;

namespace Vault;

public interface IVaultApiClientProvider
{
    TClient GetAnonymous<TClient>(ServerEndpoints endpoints)
        where TClient : class, IAnonymousApiRole =>
        GetAnonymous<TClient>(endpoints.WebVault.Origin);

    TClient GetAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole;

    TClient GetAuthenticated<TClient>(VaultSessionLease lease)
        where TClient : class, IAuthenticatedApiRole;
}
