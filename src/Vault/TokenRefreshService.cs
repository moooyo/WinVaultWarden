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

    // 兼容入口:捕获一次当前 lease 后委托给 lease 重载(不含第二份刷新逻辑)。
    public Task<bool> TryRefreshAsync(CancellationToken ct = default) =>
        TryRefreshAsync(_session.CaptureLease(), ct);

    public async Task<bool> TryRefreshAsync(
        VaultSessionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (!_session.TryGetTokenSnapshot(
                lease, out var beforeAccess, out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
            return false;

        // 只有 caller 的 token 取消才向上传播;lease 过期导致的取消返回 false,不重试旧请求。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!_session.TryGetTokenSnapshot(
                    lease, out var currentAccess, out refreshToken))
                return false;
            if (!string.Equals(currentAccess, beforeAccess, StringComparison.Ordinal))
                return true;
            if (string.IsNullOrWhiteSpace(refreshToken)
                || !_tokenStore.TryLoad(out var persisted)
                || persisted.AccountId != lease.AccountId
                || persisted.Origin != lease.Origin
                || persisted.Generation != lease.Generation)
                return false;

            var api = _clients.GetAnonymous<IIdentityApiClient>(lease.Origin);
            var result = await api.ConnectTokenAsync(
                ConnectTokenRequest.Refresh(
                    refreshToken!,
                    persisted.DeviceIdentifier,
                    DeviceName),
                linked.Token).ConfigureAwait(false);

            if (result is ConnectTokenResult.Success success)
            {
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

            // B1：仅当服务端明确判定 refresh token 失效（invalid_grant，见 vaultwarden identity.rs）
            // 且原始 lease 仍 current 时，rotation-first 锁定当前会话并清敏感状态。
            // 陈旧 lease 的响应（stale A）绝不锁定已切换的新会话（B）。
            if (result is ConnectTokenResult.Error { ErrorCode: "invalid_grant" })
                _session.TryCommit(lease, () => _session.Lock());
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or JsonException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}

