using System.Text.Json.Serialization;
using Core.Models;

namespace App.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedAccountDeletionTombstoneLedger(
    string Format,
    int SchemaVersion,
    IReadOnlyList<PersistedAccountDeletionTombstoneEntry> Entries)
{
    public const string FormatName = "WinVaultWarden.AccountDeletionTombstoneLedger";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 32;

    public static PersistedAccountDeletionTombstoneLedger Empty { get; } = new(
        FormatName,
        CurrentSchemaVersion,
        []);

    public static PersistedAccountDeletionTombstoneLedger Create(
        IEnumerable<AccountDeletionTombstoneRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var ledger = new PersistedAccountDeletionTombstoneLedger(
            FormatName,
            CurrentSchemaVersion,
            records.Select(PersistedAccountDeletionTombstoneEntry.From).ToArray());
        return ledger.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("The account-deletion tombstone ledger is invalid.");
    }

    public bool TryNormalize(out PersistedAccountDeletionTombstoneLedger normalized)
    {
        normalized = null!;
        if (!string.Equals(Format, FormatName, StringComparison.Ordinal)
            || SchemaVersion != CurrentSchemaVersion
            || Entries is null
            || Entries.Count > MaximumEntries)
        {
            return false;
        }

        var entries = new List<PersistedAccountDeletionTombstoneEntry>(Entries.Count);
        var entryIds = new HashSet<Guid>();
        var deletionScopes = new HashSet<DeletionScopeKey>();
        foreach (var candidate in Entries)
        {
            if (candidate is null
                || !candidate.TryNormalize(out var entry)
                || !entryIds.Add(entry.EntryId)
                || !deletionScopes.Add(DeletionScopeKey.From(entry)))
            {
                return false;
            }
            entries.Add(entry);
        }

        normalized = new PersistedAccountDeletionTombstoneLedger(
            FormatName,
            CurrentSchemaVersion,
            entries);
        return true;
    }

    public IReadOnlyList<AccountDeletionTombstoneRecord> ToRecords() =>
        Entries.Select(entry => entry.ToRecord()).ToArray();

    // Account deletion is a server-side account operation. Email and device identifiers remain
    // part of the evidence payload, but cannot split the authoritative deletion scope.
    private sealed record DeletionScopeKey(
        string AccountId,
        string WebVault,
        string Api,
        string Identity,
        string Notifications,
        string Icons,
        string SendWeb)
    {
        public static DeletionScopeKey From(PersistedAccountDeletionTombstoneEntry entry) =>
            new(
                entry.AccountId,
                entry.WebVault,
                entry.Api,
                entry.Identity,
                entry.Notifications,
                entry.Icons,
                entry.SendWeb);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedAccountDeletionTombstoneEntry(
    Guid EntryId,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int Stage,
    string AccountId,
    string NormalizedEmail,
    string WebVault,
    string Api,
    string Identity,
    string Notifications,
    string Icons,
    string SendWeb,
    string DeviceIdentifier)
{
    public static PersistedAccountDeletionTombstoneEntry From(
        AccountDeletionTombstoneRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var binding = record.Binding;
        var endpoints = binding.Endpoints;
        var entry = new PersistedAccountDeletionTombstoneEntry(
            record.EntryId,
            record.Revision,
            record.CreatedAt,
            record.UpdatedAt,
            (int)record.Stage,
            binding.AccountId,
            binding.NormalizedEmail,
            endpoints.WebVault.Value,
            endpoints.Api.Value,
            endpoints.Identity.Value,
            endpoints.Notifications.Value,
            endpoints.Icons.Value,
            endpoints.SendWeb.Value,
            binding.DeviceIdentifier);
        return entry.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("The account-deletion tombstone entry is invalid.");
    }

    public bool TryNormalize(out PersistedAccountDeletionTombstoneEntry normalized)
    {
        normalized = null!;
        if (EntryId == Guid.Empty
            || Revision <= 0
            || !Enum.IsDefined(typeof(AccountDeletionStage), Stage)
            || CreatedAt.Offset != TimeSpan.Zero
            || UpdatedAt.Offset != TimeSpan.Zero
            || CreatedAt < DateTimeOffset.UnixEpoch
            || UpdatedAt < CreatedAt
            || Stage == (int)AccountDeletionStage.Prepared && UpdatedAt != CreatedAt)
        {
            return false;
        }

        try
        {
            var endpoints = ToEndpoints();
            var binding = new AccountDeletionBinding(
                AccountId,
                NormalizedEmail,
                endpoints,
                DeviceIdentifier);
            var record = new AccountDeletionTombstoneRecord(
                EntryId,
                Revision,
                CreatedAt,
                UpdatedAt,
                binding,
                (AccountDeletionStage)Stage);
            if (!HasCanonicalFields(record))
                return false;

            normalized = new PersistedAccountDeletionTombstoneEntry(
                record.EntryId,
                record.Revision,
                record.CreatedAt,
                record.UpdatedAt,
                (int)record.Stage,
                record.Binding.AccountId,
                record.Binding.NormalizedEmail,
                endpoints.WebVault.Value,
                endpoints.Api.Value,
                endpoints.Identity.Value,
                endpoints.Notifications.Value,
                endpoints.Icons.Value,
                endpoints.SendWeb.Value,
                record.Binding.DeviceIdentifier);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public AccountDeletionTombstoneRecord ToRecord() => new(
        EntryId,
        Revision,
        CreatedAt,
        UpdatedAt,
        new AccountDeletionBinding(
            AccountId,
            NormalizedEmail,
            ToEndpoints(),
            DeviceIdentifier),
        (AccountDeletionStage)Stage);

    public ServerEndpoints ToEndpoints() => new(
        new ServerEndpoint(WebVault),
        new ServerEndpoint(Api),
        new ServerEndpoint(Identity),
        new ServerEndpoint(Notifications),
        new ServerEndpoint(Icons),
        new ServerEndpoint(SendWeb));

    private bool HasCanonicalFields(AccountDeletionTombstoneRecord record)
    {
        var binding = record.Binding;
        var endpoints = binding.Endpoints;
        return string.Equals(AccountId, binding.AccountId, StringComparison.Ordinal)
            && string.Equals(
                NormalizedEmail,
                binding.NormalizedEmail,
                StringComparison.Ordinal)
            && string.Equals(
                DeviceIdentifier,
                binding.DeviceIdentifier,
                StringComparison.Ordinal)
            && IsCanonicalEndpoint(WebVault, endpoints.WebVault.Value)
            && IsCanonicalEndpoint(Api, endpoints.Api.Value)
            && IsCanonicalEndpoint(Identity, endpoints.Identity.Value)
            && IsCanonicalEndpoint(
                Notifications,
                endpoints.Notifications.Value)
            && IsCanonicalEndpoint(Icons, endpoints.Icons.Value)
            && IsCanonicalEndpoint(SendWeb, endpoints.SendWeb.Value);
    }

    private static bool IsCanonicalEndpoint(string? value, string canonical) =>
        value is not null
        && value.Length <= AccountDeletionBinding.MaximumEndpointLength
        && string.Equals(value, canonical, StringComparison.Ordinal);
}
