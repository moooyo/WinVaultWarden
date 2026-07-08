using System.Net.Http;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Services;

namespace Vault;

public sealed class TokenRefreshService : ITokenRefresher
{
    private const string DeviceName = "WinVaultWarden";

    private readonly IReadonlyApiClient _api;
    private readonly IApiEndpointConfig _apiConfig;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TokenRefreshService(IReadonlyApiClient api, IApiEndpointConfig apiConfig, VaultSession session, ITokenStore tokenStore)
    {
        _api = api;
        _apiConfig = apiConfig;
        _session = session;
        _tokenStore = tokenStore;
    }

    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        var before = _session.AccessToken;
        var refreshToken = _session.RefreshToken;
        if (string.IsNullOrEmpty(refreshToken))
            return false;

        await _gate.WaitAsync(ct);
        try
        {
            // 双检:别的并发请求可能已经刷过,直接复用其结果。
            if (!string.Equals(_session.AccessToken, before, StringComparison.Ordinal))
                return true;

            if (!_tokenStore.TryLoad(out var persisted))
                return false;

            _apiConfig.SetBaseAddress(persisted.ServerUrl);
            var result = await _api.ConnectTokenAsync(
                ConnectTokenRequest.Refresh(refreshToken, persisted.DeviceIdentifier, DeviceName), ct);

            if (result is ConnectTokenResult.Success success)
            {
                _session.SetTokens(success.Token.AccessToken, success.Token.RefreshToken);
                _tokenStore.Save(persisted with { RefreshToken = success.Token.RefreshToken });
                return true;
            }

            // B1:仅当服务端明确判定 refresh token 失效(invalid_grant,见 vaultwarden identity.rs:145/157)
            // 才锁库;瞬时故障(HTTP 5xx/429/408、反代 HTML、网络异常)不锁,返回 false,待下次 401/重连重试。
            // 网络异常由下方 catch 处理(同样不锁)。
            if (result is ConnectTokenResult.Error { ErrorCode: "invalid_grant" })
                _session.Lock();
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
