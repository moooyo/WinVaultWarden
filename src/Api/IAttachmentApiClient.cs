using Api.Dtos;

namespace Api;

// 附件端点。读 GET api/ciphers/{id} 拿含附件的 CipherDto;两步上传(v2 元数据 + multipart 文件体);
// 删除 DELETE api/ciphers/{id}/attachment/{attId}。上传 url 与下载 url 由服务端给出(可能为相对路径)。
public interface IAttachmentApiClient
{
    void SetBaseAddress(string baseUrl);
    // GET api/ciphers/{id}:返回含新鲜 url 与各附件 key 的单条目。
    Task<CipherDto> GetCipherAsync(string cipherId, CancellationToken ct = default);
    // 第1步:POST api/ciphers/{cipherId}/attachment/v2,返回 attachmentId 与相对上传 url。
    Task<AttachmentUploadV2Response> CreateAttachmentV2Async(string cipherId, AttachmentUploadRequest request, CancellationToken ct = default);
    // 第2步:POST {uploadUrl}(经 NormalizeServerPath 补 api/),multipart 字段名 data,part filename=加密后的 fileName。
    Task UploadAttachmentDataAsync(string uploadUrl, string encryptedFileName, byte[] encryptedBuffer, CancellationToken ct = default);
    Task DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default);
    // GET 附件 url(可能绝对、可能相对)取原始加密字节。
    Task<byte[]> DownloadAttachmentBytesAsync(string downloadUrl, CancellationToken ct = default);
}
