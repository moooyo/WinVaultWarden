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

            ConnectTokenResult result;
            try
            {
                result = await api.ConnectTokenAsync(
                    ConnectTokenRequest.Refresh(
                        refreshToken!,
                        persisted.DeviceIdentifier,
                        DeviceName),
                    ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return false;
            }

            // B1：仅当服务端明确判定 refresh token 失效（invalid_grant，见 vaultwarden identity.rs）
            // 且原始 lease 仍 current 时，rotation-first 锁定当前会话并清敏感状态。
            // 陈旧 lease 的响应（stale A）绝不锁定已切换的新会话（B）。
            if (result is ConnectTokenResult.Error { ErrorCode: "invalid_grant" })
            {
                _session.TryLockIfCurrent(lease);
                return false;
            }
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

