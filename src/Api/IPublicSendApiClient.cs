using Api.Dtos;

namespace Api;

public interface IPublicSendApiClient : IAnonymousApiRole
{
    Task<SendAccessTokenResult> RequestSendAccessTokenAsync(
        string accessId, string? passwordProof, CancellationToken ct = default);
    Task<SendAccessResponseDto> AccessSendWithTokenAsync(
        string accessToken, CancellationToken ct = default);
    Task<SendFileDownloadResponse> AccessSendFileWithTokenAsync(
        string fileId, string accessToken, CancellationToken ct = default);

    // Legacy Vaultwarden access endpoints.
    Task<SendAccessResponseDto> AccessSendAsync(
        string accessId, string? passwordProof, CancellationToken ct = default);
    Task<SendFileDownloadResponse> AccessSendFileAsync(
        string sendId, string fileId, string? passwordProof, CancellationToken ct = default);
    Task<DownloadContent> OpenSendFileDownloadAsync(
        string downloadUrl, CancellationToken ct = default);
}
