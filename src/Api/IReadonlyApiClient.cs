using Api.Dtos;

namespace Api;

public interface IReadonlyApiClient
{
    void SetBaseAddress(string baseUrl);
    Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default);
    Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default);
    Task<ConnectTokenResult> ConnectTokenAsync(ConnectTokenRequest request, CancellationToken ct = default);
    Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default);
    Task<SyncResponse> GetSyncAsync(CancellationToken ct = default);
    Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default);
}
