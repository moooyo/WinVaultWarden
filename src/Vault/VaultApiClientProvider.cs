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
    private readonly Func<VaultSessionLease, AccessTokenBinding, CancellationToken, Task<bool>> _refreshAsync;
    private readonly object _gate = new();
    private readonly Dictionary<(ServerEndpoints Endpoints, Type Role), object> _anonymous = [];
    private Guid? _authenticatedStamp;
    private IAuthenticatedApiClient? _authenticated;
    private bool _disposed;

    public VaultApiClientProvider(
        IApiClientFactory factory,
        VaultSession session,
        Func<VaultSessionLease, AccessTokenBinding, CancellationToken, Task<bool>> refreshAsync)
    {
        _factory = factory;
        _session = session;
        _refreshAsync = refreshAsync;
    }

    public VaultApiClientProvider(
        IApiClientFactory factory,
        VaultSession session,
        Func<VaultSessionLease, CancellationToken, Task<bool>> refreshAsync)
        : this(factory, session, (lease, _, ct) => refreshAsync(lease, ct))
    {
    }

    public TClient GetAnonymous<TClient>(ServerEndpoints endpoints)
        where TClient : class, IAnonymousApiRole =>
        GetOrCreateAnonymous<TClient>(endpoints);

    public TClient GetAnonymous<TClient>(ServerOrigin origin)
        where TClient : class, IAnonymousApiRole =>
        GetOrCreateAnonymous<TClient>(ServerEndpoints.ForSelfHosted(origin.Value));

    private TClient GetOrCreateAnonymous<TClient>(ServerEndpoints endpoints)
        where TClient : class, IAnonymousApiRole
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        lock (_gate)
        {
            ThrowIfDisposed();
            var key = (endpoints, typeof(TClient));
            if (!_anonymous.TryGetValue(key, out var client))
            {
                client = _factory.CreateAnonymous<TClient>(endpoints);
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
        ArgumentNullException.ThrowIfNull(lease);
        _session.ThrowIfExpired(lease);

        IAuthenticatedApiClient? clientToDispose = null;
        TClient? resolved = null;
        Exception? failure = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            _session.ThrowIfExpired(lease);
            if (_authenticatedStamp == lease.Stamp && _authenticated is not null)
            {
                return _authenticated as TClient
                    ?? throw UnsupportedAuthenticatedRole<TClient>();
            }

            var created = _factory.CreateAuthenticated(
                lease.Endpoints,
                () => _session.GetAccessTokenBinding(lease),
                (failed, ct) => _refreshAsync(lease, failed, ct));
            if (created is not TClient typed)
            {
                clientToDispose = created;
                failure = UnsupportedAuthenticatedRole<TClient>();
            }
            else if (!_session.IsCurrent(lease))
            {
                clientToDispose = created;
                failure = new OperationCanceledException(
                    "Vault session lease expired during API client creation.",
                    lease.CancellationToken);
            }
            else
            {
                clientToDispose = _authenticated;
                _authenticated = created;
                _authenticatedStamp = lease.Stamp;
                resolved = typed;
                if (ReferenceEquals(clientToDispose, created))
                    clientToDispose = null;
            }
        }

        clientToDispose?.Dispose();
        if (failure is not null)
            throw failure;
        return resolved!;
    }

    public void Dispose()
    {
        object[] anonymous;
        IAuthenticatedApiClient? authenticated;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            anonymous = _anonymous.Values.ToArray();
            _anonymous.Clear();
            authenticated = _authenticated;
            _authenticated = null;
            _authenticatedStamp = null;
        }

        foreach (var client in anonymous.OfType<IDisposable>())
            client.Dispose();
        authenticated?.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static NotSupportedException UnsupportedAuthenticatedRole<TClient>() =>
        new($"Unsupported authenticated API role: {typeof(TClient).FullName}");
}
