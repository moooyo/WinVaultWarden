using Core.Models;

namespace Core.Services;

public sealed class CipherCreateOutcomeUncertainException : Exception
{
    public CipherCreateOutcomeUncertainException(
        string message,
        CipherCreatePending pending,
        Exception? mutationException,
        Exception refreshException)
        : base(message, refreshException)
    {
        Pending = pending ?? throw new ArgumentNullException(nameof(pending));
        MutationException = mutationException;
        RefreshException = refreshException ?? throw new ArgumentNullException(nameof(refreshException));
    }

    public CipherCreatePending Pending { get; }
    public Exception? MutationException { get; }
    public Exception RefreshException { get; }
}
