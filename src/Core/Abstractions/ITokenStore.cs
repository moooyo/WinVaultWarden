using Core.Models;

namespace Core.Abstractions;

public interface ITokenStore
{
    bool TryLoad(out PersistedSession session);

    /// <summary>
    /// Loads an exact saved account without changing the active account or MRU order.
    /// </summary>
    bool TryLoad(
        string accountId,
        ServerEndpoints endpoints,
        out PersistedSession session)
    {
        if (TryLoad(out session)
            && session.AccountId == accountId
            && session.Endpoints == endpoints)
        {
            return true;
        }

        session = null!;
        return false;
    }

    void Save(PersistedSession session);
    void Clear();

    /// <summary>
    /// Returns display and routing metadata in most-recently-used order.
    /// Implementations must not load inactive authentication material while
    /// producing this list.
    /// </summary>
    IReadOnlyList<PersistedAccountMetadata> GetSavedAccounts() =>
        TryLoad(out var session)
            ? [PersistedAccountMetadata.FromSession(session, isActive: true)]
            : [];

    bool TryActivate(
        string accountId,
        ServerEndpoints endpoints,
        out PersistedSession session)
    {
        if (TryLoad(out session)
            && session.AccountId == accountId
            && session.Endpoints == endpoints)
            return true;

        session = null!;
        return false;
    }

    /// <summary>
    /// Removes the exact account binding. Removal is idempotent: an already
    /// absent binding is a successful outcome.
    /// </summary>
    bool Remove(string accountId, ServerEndpoints endpoints)
    {
        if (!TryLoad(out var session))
            return true;
        if (session.AccountId != accountId || session.Endpoints != endpoints)
            return true;

        Clear();
        return true;
    }
}
