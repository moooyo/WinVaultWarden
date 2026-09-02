using Core.Abstractions;
using Core.Models;

namespace Vault;

public sealed class MemoryTokenStore : ITokenStore
{
    private readonly object _gate = new();
    private readonly List<PersistedSession> _sessions = [];

    public bool TryLoad(out PersistedSession session)
    {
        lock (_gate)
        {
            if (_sessions.Count == 0 || !_sessions[0].TryMigrate(out session))
            {
                session = null!;
                return false;
            }
            _sessions[0] = session;
            return true;
        }
    }

    public bool TryLoad(
        string accountId,
        ServerEndpoints endpoints,
        out PersistedSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        lock (_gate)
        {
            var candidate = _sessions.FirstOrDefault(value =>
                value.AccountId == accountId && value.Endpoints == endpoints);
            if (candidate is null || !candidate.TryMigrate(out session))
            {
                session = null!;
                return false;
            }

            return true;
        }
    }

    public void Save(PersistedSession session)
    {
        if (!session.TryMigrate(out var normalized))
            throw new InvalidDataException("Persisted session binding is invalid.");
        lock (_gate)
        {
            RemoveNoLock(normalized.AccountId, normalized.Endpoints!);
            _sessions.Insert(0, normalized);
            if (_sessions.Count > PersistedSessionCollection.MaximumSessions)
                _sessions.RemoveRange(
                    PersistedSessionCollection.MaximumSessions,
                    _sessions.Count - PersistedSessionCollection.MaximumSessions);
        }
    }

    public void Clear()
    {
        lock (_gate)
            _sessions.Clear();
    }

    public IReadOnlyList<PersistedAccountMetadata> GetSavedAccounts()
    {
        lock (_gate)
            return _sessions
                .Select((session, index) =>
                    PersistedAccountMetadata.FromSession(session, index == 0))
                .ToArray();
    }

    public bool TryActivate(
        string accountId,
        ServerEndpoints endpoints,
        out PersistedSession session)
    {
        lock (_gate)
        {
            var index = _sessions.FindIndex(candidate =>
                candidate.AccountId == accountId && candidate.Endpoints == endpoints);
            if (index < 0)
            {
                session = null!;
                return false;
            }

            session = _sessions[index];
            if (index > 0)
            {
                _sessions.RemoveAt(index);
                _sessions.Insert(0, session);
            }
            return true;
        }
    }

    public bool Remove(string accountId, ServerEndpoints endpoints)
    {
        lock (_gate)
        {
            RemoveNoLock(accountId, endpoints);
            return true;
        }
    }

    private bool RemoveNoLock(string accountId, ServerEndpoints endpoints)
    {
        var index = _sessions.FindIndex(candidate =>
            candidate.AccountId == accountId && candidate.Endpoints == endpoints);
        if (index < 0)
            return false;
        _sessions.RemoveAt(index);
        return true;
    }
}
