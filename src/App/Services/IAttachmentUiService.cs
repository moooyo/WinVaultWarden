using App.ViewModels.Models;
using Core.Models;

namespace App.Services;

// Maps Core attachment results to App view models while preserving streaming file boundaries.
public interface IAttachmentUiService
{
    Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(
        string cipherId,
        ReopenableFileSource file,
        CancellationToken ct = default);
    Task<long> DownloadAttachmentAsync(
        string cipherId,
        string attachmentId,
        Stream plaintextDestination,
        CancellationToken ct = default);
    Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default);
}
