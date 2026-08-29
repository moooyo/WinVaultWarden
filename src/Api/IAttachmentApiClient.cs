using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Session;

namespace Api;

// Attachment endpoints use a two-step upload: authenticated metadata creation followed by either
// a direct multipart transfer or an Azure SAS upload. Upload and download URLs come from the server.
public interface IAttachmentApiClient : IAuthenticatedApiRole
{
    // Returns a fresh attachment URL and key metadata for the cipher.
    Task<CipherDto> GetCipherAsync(string cipherId, CancellationToken ct = default);
    // Creates attachment metadata and captures the binding used by the successful request.
    Task<AuthenticatedWriteReceipt<AttachmentUploadV2Response>> CreateAttachmentV2Async(
        string cipherId,
        AttachmentUploadRequest request,
        CancellationToken ct = default);
    // Streams the encrypted payload without buffering it in memory. Direct uploads use multipart;
    // Azure uploads use a raw Block Blob PUT.
    Task UploadAttachmentDataAsync(
        FileUploadType fileUploadType,
        string uploadUrl,
        string cipherId,
        string attachmentId,
        string encryptedFileName,
        StreamUploadContent encryptedContent,
        CancellationToken ct = default);
    Task DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default);
    Task<DownloadContent> OpenAttachmentDownloadAsync(
        string downloadUrl,
        CancellationToken ct = default);
}
