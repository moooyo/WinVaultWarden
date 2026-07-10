using Api;
using Core.Models;
using Core.Session;

namespace Vault;

public interface IVaultApiClientProvider
{
    TClient GetAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole;

    TClient GetAuthenticated<TClient>(VaultSessionLease lease)
        where TClient : class, IAuthenticatedApiRole;
}
