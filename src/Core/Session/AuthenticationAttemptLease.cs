namespace Core.Session;

/// <summary>
/// An unforgeable capability for one authentication attempt. The lease is captured before any
/// asynchronous authentication work starts and can be promoted to a session only while current.
/// </summary>
public sealed class AuthenticationAttemptLease
{
    internal AuthenticationAttemptLease(
        long generation,
        CancellationToken cancellationToken,
        Guid stamp)
    {
        Generation = generation;
        CancellationToken = cancellationToken;
        Stamp = stamp;
    }

    public long Generation { get; }
    public CancellationToken CancellationToken { get; }
    internal Guid Stamp { get; }
}
