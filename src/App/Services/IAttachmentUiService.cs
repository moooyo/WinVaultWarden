using App.ViewModels.Models;

namespace App.Services;

// App 层附件 UI 服务：把 Core 的 IAttachmentService(返回 Core.Models.CipherAttachment)
// 映射为视图模型 AttachmentItem。UI 仅依赖本接口，便于 App.Tests 链接与替换。
public interface IAttachmentUiService
{
    Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(string cipherId, byte[] fileBytes, string fileName, CancellationToken ct = default);
    Task<byte[]> DownloadAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default);
    Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default);
}
