using Api.Dtos;

namespace Api;

public interface IPublicSendApiClient : IAnonymousApiRole
{
    Task<SendAccessResponseDto> AccessSendAsync(
        string accessId, string? passwordProof, CancellationToken ct = default);
    Task<SendFileDownloadResponse> AccessSendFileAsync(
        string sendId, string fileId, string? passwordProof, CancellationToken ct = default);
    Task<byte[]> DownloadSendFileBytesAsync(
        string downloadUrl, CancellationToken ct = default);
}
