using Core.Models;
using Core.Session;

namespace Core.Services;

public interface ISyncService
{
    Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Cipher>> SyncAsync(
        VaultSessionLease lease,
        CancellationToken ct = default);
}
