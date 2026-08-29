using Core.Abstractions;
using Core.Models;

namespace Vault;

public sealed class MemoryTokenStore : ITokenStore
{
    private PersistedSession? _session;

    public bool TryLoad(out PersistedSession session)
    {
        if (_session is null || !_session.TryMigrate(out session))
        {
            session = null!;
            return false;
        }
        _session = session;
        return true;
    }

    public void Save(PersistedSession session)
    {
        if (!session.TryMigrate(out var normalized))
            throw new InvalidDataException("Persisted session binding is invalid.");
        _session = normalized;
    }

    public void Clear() => _session = null;
}
