using Core.Models;
using Core.Session;

namespace Core.Services;

public interface ISyncService
{
    Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Cipher>> SyncAsync(
        VaultSessionLease lease,
        CancellationToken ct = default);

    async Task<bool> SyncIfNeededAsync(
        VaultSessionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await SyncAsync(lease, ct);
        return true;
    }

    async Task<CipherCreateReconciliationResult> SyncAndReconcileCipherCreateAsync(
        VaultSessionLease lease,
        CipherCreatePending pending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(pending);
        var ciphers = await SyncAsync(lease, ct);
        return CipherCreateReconciler.Reconcile(
            pending,
            lease.AccountId,
            lease.Endpoints,
            ciphers.Select(CipherCreateObservation.FromCipher));
    }
}
