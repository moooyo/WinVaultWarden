using App.ViewModels.Models;
using Core.Models;
using Core.Services;

namespace App.Services;

// Delegates streaming attachment work to Core and maps metadata into App view models.
public sealed class AttachmentUiService : IAttachmentUiService
{
    private readonly IAttachmentService _attachments;

    public AttachmentUiService(IAttachmentService attachments)
    {
        _attachments = attachments;
    }

    public async Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(
        string cipherId,
        ReopenableFileSource file,
        CancellationToken ct = default)
    {
        var result = await _attachments.UploadAsync(cipherId, file, ct);
        return Map(result);
    }

    public Task<long> DownloadAttachmentAsync(
        string cipherId,
        string attachmentId,
        Stream plaintextDestination,
        CancellationToken ct = default) =>
        _attachments.DownloadAsync(cipherId, attachmentId, plaintextDestination, ct);

    public async Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        var result = await _attachments.DeleteAsync(cipherId, attachmentId, ct);
        return Map(result);
    }

    private static IReadOnlyList<AttachmentItem> Map(IReadOnlyList<CipherAttachment> attachments) =>
        attachments.Select(a => new AttachmentItem(a.Id, a.FileName, a.SizeName)).ToList();
}
