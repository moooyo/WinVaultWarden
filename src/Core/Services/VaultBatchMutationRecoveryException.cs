namespace Core.Services;

/// <summary>
/// Raised when a bulk mutation may have partially committed and the authoritative refresh also failed.
/// </summary>
public sealed class VaultBatchMutationRecoveryException : Exception
{
    public VaultBatchMutationRecoveryException(
        string message,
        Exception mutationException,
        Exception refreshException)
        : base(message, new AggregateException(mutationException, refreshException))
    {
        MutationException = mutationException;
        RefreshException = refreshException;
    }

    public Exception MutationException { get; }
    public Exception RefreshException { get; }
}
