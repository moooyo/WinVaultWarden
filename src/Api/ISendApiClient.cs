using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Session;

namespace Api;

// Send 端点。读操作直接反序列化响应;写操作 4xx 统一抛 VaultWriteException。
// 路径前缀 /api,但文件上传 url 与下载 url 由服务端给出(可能是相对路径)。
public interface ISendApiClient : IAuthenticatedApiRole
{
    Task<SendListResponse> GetSendsAsync(CancellationToken ct = default);
    Task<SendResponseDto> CreateTextSendAsync(SendRequest request, CancellationToken ct = default);
    Task<AuthenticatedWriteReceipt<SendFileUploadV2Response>> CreateFileSendV2Async(
        SendRequest request,
        CancellationToken ct = default);
    // 文件体走 streaming 上传:只接受可流式写入的 StreamUploadContent,绝不接受整份 byte[](见 Task 12)。
    Task UploadSendFileAsync(
        FileUploadType fileUploadType,
        string uploadUrl,
        string sendId,
        string fileId,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        CancellationToken ct = default);
    Task<SendResponseDto> UpdateSendAsync(string sendId, SendRequest request, CancellationToken ct = default);
    Task DeleteSendAsync(string sendId, CancellationToken ct = default);
    Task DeleteSendForReplacementAsync(string sendId, CancellationToken ct = default);
    Task<SendResponseDto> RemoveSendPasswordAsync(string sendId, CancellationToken ct = default);
}
