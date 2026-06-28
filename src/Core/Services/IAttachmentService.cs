using Core.Models;

namespace Core.Services;

// 附件子系统编排:加密上传 → 下载解密 → 删除。
// 所有方法要求 vault 处于解锁态,否则抛 InvalidOperationException。
public interface IAttachmentService
{
    // 加密并以 v2 两步流程上传 fileName/plaintext,完成后整库重同步,返回该条目的最新附件列表。
    Task<IReadOnlyList<CipherAttachment>> UploadAsync(string cipherId, string fileName, byte[] plaintext, CancellationToken ct = default);

    // 拉取新鲜 url 与附件密钥,下载密文并解密为明文字节。
    Task<byte[]> DownloadAsync(string cipherId, string attachmentId, CancellationToken ct = default);

    // 删除指定附件后整库重同步,返回该条目的最新附件列表。
    Task<IReadOnlyList<CipherAttachment>> DeleteAsync(string cipherId, string attachmentId, CancellationToken ct = default);
}
