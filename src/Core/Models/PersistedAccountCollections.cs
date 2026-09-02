namespace Core.Models;

public sealed record PersistedSessionCollection(
    int SchemaVersion,
    IReadOnlyList<PersistedSession> Sessions)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSessions = 5;

    public bool TryNormalize(out PersistedSessionCollection normalized)
    {
        normalized = null!;
        if (SchemaVersion != CurrentSchemaVersion || Sessions is null)
            return false;

        var sessions = new List<PersistedSession>(Math.Min(Sessions.Count, MaximumSessions));
        var seen = new HashSet<(string AccountId, ServerEndpoints Endpoints)>();
        foreach (var candidate in Sessions)
        {
            if (candidate is null
                || !candidate.TryMigrate(out var session)
                || !seen.Add((session.AccountId, session.Endpoints!)))
                continue;

            sessions.Add(session);
            if (sessions.Count == MaximumSessions)
                break;
        }

        if (sessions.Count == 0)
            return false;

        normalized = new PersistedSessionCollection(CurrentSchemaVersion, sessions);
        return true;
    }

    public static PersistedSessionCollection FromMostRecent(
        IEnumerable<PersistedSession> sessions)
    {
        var collection = new PersistedSessionCollection(
            CurrentSchemaVersion,
            sessions.ToArray());
        return collection.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("No valid persisted sessions were supplied.");
    }
}

public sealed record VaultCacheCollection(
    int SchemaVersion,
    IReadOnlyList<VaultCacheEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = PersistedSessionCollection.MaximumSessions;

    public bool TryNormalize(out VaultCacheCollection normalized)
    {
        normalized = null!;
        if (SchemaVersion != CurrentSchemaVersion || Entries is null)
            return false;

        var entries = new List<VaultCacheEntry>(Math.Min(Entries.Count, MaximumEntries));
        var seen = new HashSet<(string AccountId, ServerEndpoints Endpoints)>();
        foreach (var candidate in Entries)
        {
            if (candidate is null
                || !candidate.TryMigrate(out var entry)
                || !seen.Add((entry.AccountId, entry.Endpoints!)))
                continue;

            entries.Add(entry);
            if (entries.Count == MaximumEntries)
                break;
        }

        if (entries.Count == 0)
            return false;

        normalized = new VaultCacheCollection(CurrentSchemaVersion, entries);
        return true;
    }

    public static VaultCacheCollection FromMostRecent(IEnumerable<VaultCacheEntry> entries)
    {
        var collection = new VaultCacheCollection(CurrentSchemaVersion, entries.ToArray());
        return collection.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("No valid vault cache entries were supplied.");
    }
}
