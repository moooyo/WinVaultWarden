namespace Core.Services;

/// <summary>
/// Raised when transferring a legacy cipher would make its attachments undecryptable.
/// </summary>
public sealed class LegacyCipherAttachmentTransferException : InvalidOperationException
{
    public LegacyCipherAttachmentTransferException(string cipherId)
        : base(
            $"Cipher '{cipherId}' has attachments but no item key. " +
            "Transferring it would make its attachments undecryptable.")
    {
        CipherId = cipherId;
    }

    public string CipherId { get; }
}
