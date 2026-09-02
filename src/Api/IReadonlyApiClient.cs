using Api.Dtos;

namespace Api;

public interface IReadonlyApiClient : IAuthenticatedApiRole
{
    Task<SyncResponse> GetSyncAsync(CancellationToken ct = default);
    Task<long> GetAccountRevisionDateAsync(CancellationToken ct = default) =>
        Task.FromException<long>(new NotSupportedException(
            "The API client does not implement account revision dates."));
    Task<ListResponse<DeviceDto>> GetDevicesAsync(CancellationToken ct = default);
    Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default);
    Task<SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default);
}
