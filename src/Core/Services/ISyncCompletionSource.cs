using Core.Session;

namespace Core.Services;

/// <summary>Publishes successful snapshot commits without exposing decrypted vault data.</summary>
public interface ISyncCompletionSource
{
    event Action<VaultSessionLease>? SyncCompleted;
}
