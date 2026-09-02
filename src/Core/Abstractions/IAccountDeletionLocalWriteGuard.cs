using Core.Models;

namespace Core.Abstractions;

/// <summary>
/// Prevents account-bound persistent state from being created or updated after an account
/// deletion attempt has established durable recovery state for the same server scope.
/// </summary>
public interface IAccountDeletionLocalWriteGuard
{
    /// <summary>
    /// Attempts to acquire a zero-wait write lease for one exact account and complete endpoint
    /// set. Returns null when coordination is busy or deletion state cannot safely permit writes.
    /// The lease must cover the caller's complete persistent read-modify-write operation.
    /// </summary>
    IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints);
}
