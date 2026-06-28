using App.ViewModels.Models;
using Core.Models;
using Core.Services;

namespace App.Services;

// 真实附件 UI 服务：委派给 Core.IAttachmentService，并把返回的 CipherAttachment 列表
// 映射为 AttachmentItem(Id, FileName, SizeName)。加解密、上限校验、re-sync 均在 Core/Vault 层完成。
public sealed class AttachmentUiService : IAttachmentUiService
{
    private readonly IAttachmentService _attachments;

    public AttachmentUiService(IAttachmentService attachments)
    {
        _attachments = attachments;
    }

    public async Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(string cipherId, byte[] fileBytes, string fileName, CancellationToken ct = default)
    {
        var result = await _attachments.UploadAsync(cipherId, fileName, fileBytes, ct);
        return Map(result);
    }

    public Task<byte[]> DownloadAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default) =>
        _attachments.DownloadAsync(cipherId, attachmentId, ct);

    public async Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        var result = await _attachments.DeleteAsync(cipherId, attachmentId, ct);
        return Map(result);
    }

    private static IReadOnlyList<AttachmentItem> Map(IReadOnlyList<CipherAttachment> attachments) =>
        attachments.Select(a => new AttachmentItem(a.Id, a.FileName, a.SizeName)).ToList();
}
