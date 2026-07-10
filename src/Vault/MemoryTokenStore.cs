using Core.Abstractions;
using Core.Models;

namespace Vault;

public sealed class MemoryTokenStore : ITokenStore
{
    private PersistedSession? _session;

    public bool TryLoad(out PersistedSession session)
    {
        session = _session!;
        return _session is { HasValidBinding: true };
    }

    public void Save(PersistedSession session)
    {
        if (!session.HasValidBinding)
            throw new InvalidDataException("Persisted session binding is invalid.");
        _session = session;
    }

    public void Clear() => _session = null;
}
