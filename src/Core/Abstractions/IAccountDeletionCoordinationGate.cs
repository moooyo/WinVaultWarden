namespace Core.Abstractions;

/// <summary>
/// Represents ownership of the global account-deletion coordination gate.
/// </summary>
public interface IAccountDeletionCoordinationLease : IDisposable, IAsyncDisposable
{
}

/// <summary>
/// Serializes account deletion across processes so upper layers can use the fixed ordering
/// coordination gate, durable tombstone, and account-bound stores.
/// </summary>
public interface IAccountDeletionCoordinationGate
{
    /// <summary>
    /// Attempts to acquire the global deletion gate without waiting. An active ambient owner may
    /// acquire a reentrant lease. Process-local or file-lock contention returns null; storage and
    /// permission failures are propagated.
    /// </summary>
    IAccountDeletionCoordinationLease? TryAcquire();

    /// <summary>
    /// Acquires the global deletion gate. Implementations may treat nested acquisition in the same
    /// async flow as reentrant so an HTTP 401 refresh path cannot deadlock its owning deletion.
    /// Callers must not fan out parallel work that reacquires this gate while a lease is held,
    /// because ambient async-flow ownership is inherited by child execution contexts.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// The supplied cancellation token was cancelled before ownership was acquired.
    /// </exception>
    ValueTask<IAccountDeletionCoordinationLease> AcquireAsync(
        CancellationToken cancellationToken = default);
}
