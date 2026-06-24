using Core.Abstractions;
using Core.Models;

namespace Vault;

public sealed class MemoryTokenStore : ITokenStore
{
    private PersistedSession? _session;

    public bool TryLoad(out PersistedSession session)
    {
        session = _session!;
        return _session is not null;
    }

    public void Save(PersistedSession session) => _session = session;

    public void Clear() => _session = null;
}
