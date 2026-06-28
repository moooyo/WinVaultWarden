using Api.Dtos;

namespace Api;

// Send 端点。读操作直接反序列化响应;写操作 4xx 统一抛 VaultWriteException。
// 路径前缀 /api,但文件上传 url 与下载 url 由服务端给出(可能是相对路径)。
public interface ISendApiClient
{
    void SetBaseAddress(string baseUrl);
    Task<SendListResponse> GetSendsAsync(CancellationToken ct = default);
    Task<SendResponseDto> CreateTextSendAsync(SendRequest request, CancellationToken ct = default);
    Task<SendFileUploadV2Response> CreateFileSendV2Async(SendRequest request, CancellationToken ct = default);
    Task UploadSendFileAsync(string uploadUrl, string encryptedFileName, byte[] encryptedBuffer, CancellationToken ct = default);
    Task<SendResponseDto> UpdateSendAsync(string sendId, SendRequest request, CancellationToken ct = default);
    Task DeleteSendAsync(string sendId, CancellationToken ct = default);
    Task<SendResponseDto> RemoveSendPasswordAsync(string sendId, CancellationToken ct = default);
    Task<SendAccessResponseDto> AccessSendAsync(string accessId, string? passwordProof, CancellationToken ct = default);
    // 访问文件型 Send 的下载 URL。先用此端点换取带 JWT 的实际下载地址,再 GET 下载。
    Task<SendFileDownloadResponse> AccessSendFileAsync(string sendId, string fileId, string? passwordProof, CancellationToken ct = default);
    Task<byte[]> DownloadSendFileBytesAsync(string downloadUrl, CancellationToken ct = default);
}
