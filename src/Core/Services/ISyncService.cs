using Core.Models;

namespace Core.Services;

public interface ISyncService
{
    Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default);
}
