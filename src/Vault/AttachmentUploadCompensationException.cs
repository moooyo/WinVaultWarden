namespace Vault;

/// <summary>
/// Upload failed after attachment metadata was created and the independent cleanup also failed.
/// The identifiers are exposed so the caller can report an actionable orphan without exposing
/// plaintext, keys, or tokens.
/// </summary>
public sealed class AttachmentUploadCompensationException : Exception
{
    public AttachmentUploadCompensationException(
        string cipherId,
        string orphanedAttachmentId,
        Exception operationError,
        Exception cleanupError)
        : base(
            $"Attachment upload did not complete and attachment '{orphanedAttachmentId}' may require cleanup.",
            new AggregateException(operationError, cleanupError))
    {
        CipherId = cipherId;
        OrphanedAttachmentId = orphanedAttachmentId;
    }

    public string CipherId { get; }
    public string OrphanedAttachmentId { get; }
}
