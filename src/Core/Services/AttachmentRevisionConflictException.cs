namespace Core.Services;

/// <summary>
/// Raised when attachment metadata cannot be created from the current cipher revision.
/// Synchronizing the vault produces a new baseline from which the operation may be retried.
/// </summary>
public sealed class AttachmentRevisionConflictException : InvalidOperationException
{
    public AttachmentRevisionConflictException(
        string cipherId,
        DateTimeOffset? lastKnownRevisionDate,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherId);
        CipherId = cipherId;
        LastKnownRevisionDate = lastKnownRevisionDate;
    }

    public string CipherId { get; }

    public DateTimeOffset? LastKnownRevisionDate { get; }

    public bool CanRetryAfterSync => true;
}
