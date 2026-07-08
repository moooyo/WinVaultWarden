using Api.Dtos;

namespace Api;

public interface IReadonlyApiClient
{
    Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default);
    Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default);
    Task<ConnectTokenResult> ConnectTokenAsync(ConnectTokenRequest request, CancellationToken ct = default);
    Task<SyncResponse> GetSyncAsync(CancellationToken ct = default);
    Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default);
    Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default);
    Task<SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default);

    // POST api/two-factor/send-email-login — 未认证的登录期发码端点，登录尚未拿到 token 时可用。
    Task SendTwoFactorEmailLoginAsync(string email, string masterPasswordHash, string deviceIdentifier, CancellationToken ct = default);
}
