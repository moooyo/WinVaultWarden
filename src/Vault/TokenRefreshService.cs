using System.Net.Http;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Services;
using Core.Session;

namespace Vault;

public sealed class TokenRefreshService : ITokenRefresher
{
    private const string DeviceName = "WinVaultWarden";

    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TokenRefreshService(
        IVaultApiClientProvider clients,
        VaultSession session,
        ITokenStore tokenStore)
    {
        _clients = clients;
        _session = session;
        _tokenStore = tokenStore;
    }

    public Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var api = _clients.GetAnonymous<IIdentityApiClient>(lease.Origin);
        return TryRefreshCoreAsync(lease, api, ct);
    }

    private async Task<bool> TryRefreshCoreAsync(
        VaultSessionLease lease,
        IIdentityApiClient api,
        CancellationToken ct)
    {
        if (!_session.TryGetTokenSnapshot(
                lease, out var before, out var refreshToken)
            || string.IsNullOrEmpty(refreshToken))
            return false;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.TryGetTokenSnapshot(
                    lease, out var current, out refreshToken))
                return false;
            if (!string.Equals(current, before, StringComparison.Ordinal))
                return true;
            if (!_tokenStore.TryLoad(out var persisted)
                || persisted.AccountId != lease.AccountId
                || persisted.Origin != lease.Origin)
                return false;
            var result = await api.ConnectTokenAsync(
                ConnectTokenRequest.Refresh(
                    refreshToken!,
                    persisted.DeviceIdentifier,
                    DeviceName),
                ct);
            if (result is not ConnectTokenResult.Success success)
                return false;
            return _session.TryCommit(lease, () =>
            {
                _tokenStore.Save(persisted with
                {
                    RefreshToken = success.Token.RefreshToken,
                    Generation = lease.Generation,
                });
                _session.SetTokens(
                    success.Token.AccessToken,
                    success.Token.RefreshToken);
            });
        }
        finally
        {
            _gate.Release();
        }
    }
}

