using Core.Models;

namespace App.Services;

internal sealed record PersistedSessionIndex(
    string Format,
    int SchemaVersion,
    string? ActiveBindingId,
    IReadOnlyList<PersistedSessionIndexEntry> Accounts,
    IReadOnlyList<PersistedSessionPendingDelete> PendingDeletes)
{
    public const string FormatName = "WinVaultWarden.SessionIndex";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumPendingBindings = 32;
    public const int MaximumPendingRevisionsPerBinding = 32;

    public static PersistedSessionIndex Empty { get; } = new(
        FormatName,
        CurrentSchemaVersion,
        null,
        [],
        []);

    public bool TryNormalize(out PersistedSessionIndex normalized)
    {
        normalized = null!;
        if (!string.Equals(Format, FormatName, StringComparison.Ordinal)
            || SchemaVersion != CurrentSchemaVersion
            || Accounts is null
            || PendingDeletes is null
            || Accounts.Count > PersistedSessionCollection.MaximumSessions
            || PendingDeletes.Count > MaximumPendingBindings)
        {
            return false;
        }

        var accounts = new List<PersistedSessionIndexEntry>(Accounts.Count);
        var accountBindings = new HashSet<string>(StringComparer.Ordinal);
        var referencedBlobs = new HashSet<(string BindingId, Guid Revision)>();
        foreach (var candidate in Accounts)
        {
            if (candidate is null
                || !candidate.TryNormalize(out var account)
                || !accountBindings.Add(account.BindingId)
                || !referencedBlobs.Add((account.BindingId, account.BlobRevision)))
            {
                return false;
            }
            accounts.Add(account);
        }

        if ((accounts.Count == 0 && ActiveBindingId is not null)
            || (accounts.Count > 0
                && !string.Equals(
                    ActiveBindingId,
                    accounts[0].BindingId,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        var pending = new List<PersistedSessionPendingDelete>(PendingDeletes.Count);
        var pendingBindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in PendingDeletes)
        {
            if (candidate is null
                || !candidate.TryNormalize(out var deletion)
                || !pendingBindings.Add(deletion.BindingId))
            {
                return false;
            }

            foreach (var revision in deletion.BlobRevisions)
            {
                if (referencedBlobs.Contains((deletion.BindingId, revision)))
                    return false;
            }
            pending.Add(deletion);
        }

        normalized = new PersistedSessionIndex(
            FormatName,
            CurrentSchemaVersion,
            accounts.Count == 0 ? null : accounts[0].BindingId,
            accounts,
            pending);
        return true;
    }

    public static PersistedSessionIndex Create(
        IEnumerable<PersistedSessionIndexEntry> accounts,
        IEnumerable<PersistedSessionPendingDelete>? pendingDeletes = null)
    {
        var accountArray = accounts.ToArray();
        var candidate = new PersistedSessionIndex(
            FormatName,
            CurrentSchemaVersion,
            accountArray.FirstOrDefault()?.BindingId,
            accountArray,
            pendingDeletes?.ToArray() ?? []);
        return candidate.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("Persisted session index is invalid.");
    }
}

internal sealed record PersistedSessionIndexEntry(
    string BindingId,
    string AccountId,
    string Email,
    ServerEndpoints Endpoints,
    Guid BlobRevision)
{
    public bool TryNormalize(out PersistedSessionIndexEntry normalized)
    {
        normalized = null!;
        if (string.IsNullOrWhiteSpace(Email)
            || Email.Length > 320
            || !string.Equals(Email, Email.Trim(), StringComparison.Ordinal)
            || Email.Any(char.IsControl)
            || Endpoints is null
            || BlobRevision == Guid.Empty)
        {
            return false;
        }

        try
        {
            var binding = new PersistedAccountBinding(AccountId, Endpoints);
            var expectedId = binding.CreateIdentifier();
            if (!string.Equals(BindingId, expectedId, StringComparison.Ordinal))
                return false;
            normalized = new PersistedSessionIndexEntry(
                expectedId,
                binding.AccountId,
                Email,
                binding.Endpoints,
                BlobRevision);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public PersistedAccountMetadata ToMetadata(bool isActive) => new(
        AccountId,
        Email,
        Endpoints,
        isActive);
}

internal sealed record PersistedSessionPendingDelete(
    string BindingId,
    IReadOnlyList<Guid> BlobRevisions)
{
    public bool TryNormalize(out PersistedSessionPendingDelete normalized)
    {
        normalized = null!;
        if (!PersistedSessionStorageIdentifier.IsBindingId(BindingId)
            || BlobRevisions is null
            || BlobRevisions.Count is 0 or > PersistedSessionIndex.MaximumPendingRevisionsPerBinding)
        {
            return false;
        }

        var revisions = new List<Guid>(BlobRevisions.Count);
        var seen = new HashSet<Guid>();
        foreach (var revision in BlobRevisions)
        {
            if (revision == Guid.Empty || !seen.Add(revision))
                return false;
            revisions.Add(revision);
        }

        normalized = new PersistedSessionPendingDelete(BindingId, revisions);
        return true;
    }
}

internal sealed record PersistedSessionBlobEnvelope(
    string Format,
    int SchemaVersion,
    string BindingId,
    Guid BlobRevision,
    PersistedSession Session)
{
    public const string FormatName = "WinVaultWarden.SessionBlob";
    public const int CurrentSchemaVersion = 1;

    public static PersistedSessionBlobEnvelope Create(
        PersistedSessionIndexEntry entry,
        PersistedSession session) => new(
        FormatName,
        CurrentSchemaVersion,
        entry.BindingId,
        entry.BlobRevision,
        session);

    public bool TryGetSession(
        PersistedSessionIndexEntry expected,
        out PersistedSession session)
    {
        session = null!;
        if (!string.Equals(Format, FormatName, StringComparison.Ordinal)
            || SchemaVersion != CurrentSchemaVersion
            || !string.Equals(BindingId, expected.BindingId, StringComparison.Ordinal)
            || BlobRevision != expected.BlobRevision
            || Session is null
            || !Session.TryMigrate(out var normalized)
            || !string.Equals(normalized.AccountId, expected.AccountId, StringComparison.Ordinal)
            || normalized.Endpoints != expected.Endpoints
            || !string.Equals(normalized.Email, expected.Email, StringComparison.Ordinal))
        {
            return false;
        }

        session = normalized;
        return true;
    }
}

internal static class PersistedSessionStorageIdentifier
{
    public static bool IsBindingId(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }
}
