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
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TokenRefreshService(IReadonlyApiClient api, VaultSession session, ITokenStore tokenStore)
    {
        _api = api;
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

            _api.SetBaseAddress(persisted.ServerUrl);
            var result = await _api.ConnectTokenAsync(
                ConnectTokenRequest.Refresh(refreshToken, persisted.DeviceIdentifier, DeviceName), ct);

            if (result is not ConnectTokenResult.Success success)
            {
                _session.Lock();
                return false;
            }

            _session.SetTokens(success.Token.AccessToken, success.Token.RefreshToken);
            _tokenStore.Save(persisted with { RefreshToken = success.Token.RefreshToken });
            return true;
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
