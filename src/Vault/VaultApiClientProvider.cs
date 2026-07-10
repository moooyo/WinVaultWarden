using Api;
using Core.Models;
using Core.Session;

namespace Vault;

public sealed class VaultApiClientProvider :
    IVaultApiClientProvider,
    IDisposable
{
    private readonly IApiClientFactory _factory;
    private readonly VaultSession _session;
    private readonly Func<VaultSessionLease, CancellationToken, Task<bool>> _refreshAsync;
    private readonly object _gate = new();
    private readonly Dictionary<(ServerOrigin Origin, Type Role), object> _anonymous = [];
    private Guid? _authenticatedStamp;
    private IAuthenticatedApiClient? _authenticated;

    public VaultApiClientProvider(
        IApiClientFactory factory,
        VaultSession session,
        Func<VaultSessionLease, CancellationToken, Task<bool>> refreshAsync)
    {
        _factory = factory;
        _session = session;
        _refreshAsync = refreshAsync;
    }

    public TClient GetAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole =>
        GetOrCreateAnonymous<TClient>(origin);

    private TClient GetOrCreateAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole
    {
        lock (_gate)
        {
            var key = (origin, typeof(TClient));
            if (!_anonymous.TryGetValue(key, out var client))
            {
                client = _factory.CreateAnonymous<TClient>(origin);
                _anonymous.Add(key, client);
            }
            return client as TClient
                ?? throw new NotSupportedException(
                    $"Unsupported anonymous API role: {typeof(TClient).FullName}");
        }
    }

    public TClient GetAuthenticated<TClient>(VaultSessionLease lease)
        where TClient : class, IAuthenticatedApiRole
    {
        _session.ThrowIfExpired(lease);
        lock (_gate)
        {
            if (_authenticated is null
                || _authenticatedStamp != lease.Stamp)
            {
                (_authenticated as IDisposable)?.Dispose();
                _authenticated = _factory.CreateAuthenticated(
                    lease.Origin,
                    () => _session.GetAccessTokenBinding(lease),
                    ct => _refreshAsync(lease, ct));
                _authenticatedStamp = lease.Stamp;
            }
            return _authenticated as TClient
                ?? throw new NotSupportedException(
                    $"Unsupported authenticated API role: {typeof(TClient).FullName}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _anonymous.Values.OfType<IDisposable>())
                client.Dispose();
            _anonymous.Clear();
            (_authenticated as IDisposable)?.Dispose();
            _authenticated = null;
            _authenticatedStamp = null;
        }
    }
}
