using Api;
using Core.Models;
using Core.Session;
using Vault;

namespace Vault.Tests;

public sealed class FakeVaultApiClientProvider : IVaultApiClientProvider
{
    private readonly Dictionary<Type, object> _anonymous = [];
    private readonly Dictionary<Type, object> _authenticated = [];

    public static FakeVaultApiClientProvider ForAnonymous<TClient>(TClient client)
        where TClient : class, IAnonymousApiRole =>
        new FakeVaultApiClientProvider().AddAnonymous(client);

    public static FakeVaultApiClientProvider ForAuthenticated<TClient>(TClient client)
        where TClient : class, IAuthenticatedApiRole =>
        new FakeVaultApiClientProvider().AddAuthenticated(client);

    public FakeVaultApiClientProvider AddAnonymous<TClient>(TClient client)
        where TClient : class, IAnonymousApiRole
    {
        _anonymous[typeof(TClient)] = client;
        return this;
    }

    public FakeVaultApiClientProvider AddAuthenticated<TClient>(TClient client)
        where TClient : class, IAuthenticatedApiRole
    {
        _authenticated[typeof(TClient)] = client;
        return this;
    }

    public TClient GetAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole =>
        (TClient)_anonymous[typeof(TClient)];

    public TClient GetAuthenticated<TClient>(VaultSessionLease lease)
        where TClient : class, IAuthenticatedApiRole =>
        (TClient)_authenticated[typeof(TClient)];
}
