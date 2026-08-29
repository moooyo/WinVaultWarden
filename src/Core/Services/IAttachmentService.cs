using Core.Models;

namespace Core.Services;

// Attachment orchestration keeps file bodies on streaming boundaries. All operations require an
// unlocked vault and throw InvalidOperationException otherwise.
public interface IAttachmentService
{
    Task<IReadOnlyList<CipherAttachment>> UploadAsync(
        string cipherId,
        ReopenableFileSource file,
        CancellationToken ct = default);

    Task<long> DownloadAsync(
        string cipherId,
        string attachmentId,
        Stream plaintextDestination,
        CancellationToken ct = default);

    Task<IReadOnlyList<CipherAttachment>> DeleteAsync(string cipherId, string attachmentId, CancellationToken ct = default);
}
