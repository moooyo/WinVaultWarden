using System.Text.Json.Serialization;
using Core.Abstractions;
using Core.Enums;
using Core.Models;

namespace App.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedCipherCreatePendingLedger(
    string Format,
    int SchemaVersion,
    IReadOnlyList<PersistedCipherCreatePendingEntry> Entries)
{
    public const string FormatName = "WinVaultWarden.CipherCreatePendingLedger";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 32;

    public static PersistedCipherCreatePendingLedger Empty { get; } = new(
        FormatName,
        CurrentSchemaVersion,
        []);

    public static PersistedCipherCreatePendingLedger Create(
        IEnumerable<CipherCreatePendingRecord> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var persisted = entries
            .Select(PersistedCipherCreatePendingEntry.FromRecord)
            .ToArray();
        var candidate = new PersistedCipherCreatePendingLedger(
            FormatName,
            CurrentSchemaVersion,
            persisted);
        return candidate.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("The pending-create ledger is invalid.");
    }

    public bool TryNormalize(out PersistedCipherCreatePendingLedger normalized)
    {
        normalized = null!;
        if (!string.Equals(Format, FormatName, StringComparison.Ordinal)
            || SchemaVersion != CurrentSchemaVersion
            || Entries is null
            || Entries.Count > MaximumEntries)
        {
            return false;
        }

        var entries = new List<PersistedCipherCreatePendingEntry>(Entries.Count);
        var scopes = new HashSet<PendingScope>();
        foreach (var candidate in Entries)
        {
            if (candidate is null
                || !candidate.TryNormalize(out var entry)
                || !scopes.Add(new PendingScope(entry.AccountId, entry.ToEndpoints())))
            {
                return false;
            }
            entries.Add(entry);
        }

        normalized = new PersistedCipherCreatePendingLedger(
            FormatName,
            CurrentSchemaVersion,
            entries);
        return true;
    }

    public IReadOnlyList<CipherCreatePendingRecord> ToRecords() =>
        Entries.Select(entry => entry.ToRecord()).ToArray();

    private sealed record PendingScope(string AccountId, ServerEndpoints Endpoints);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedCipherCreatePendingEntry(
    Guid ReservationId,
    long Revision,
    bool IsInFlight,
    string AccountId,
    string WebVault,
    string Api,
    string Identity,
    string Notifications,
    string Icons,
    string SendWeb,
    int CipherType,
    string? OrganizationId,
    IReadOnlyList<string> CollectionIds,
    IReadOnlyList<string> BaselineCipherIds,
    string KeyFingerprint,
    bool CommitConfirmed,
    string? ExpectedCipherId)
{
    public const int MaximumAccountIdLength = 256;
    public const int MaximumEndpointLength = 2048;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumCollectionIds = 4096;
    public const int MaximumBaselineCipherIds = 50000;

    public static PersistedCipherCreatePendingEntry FromRecord(
        CipherCreatePendingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var pending = record.Pending;
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(pending.Witness);
        var witness = pending.Witness;
        var candidate = new PersistedCipherCreatePendingEntry(
            record.ReservationId,
            record.Revision,
            record.IsInFlight,
            witness.AccountId,
            witness.Endpoints.WebVault.Value,
            witness.Endpoints.Api.Value,
            witness.Endpoints.Identity.Value,
            witness.Endpoints.Notifications.Value,
            witness.Endpoints.Icons.Value,
            witness.Endpoints.SendWeb.Value,
            (int)witness.CipherType,
            witness.OrganizationId,
            witness.CollectionIds.ToArray(),
            witness.BaselineCipherIds.ToArray(),
            witness.KeyFingerprint,
            pending.CommitConfirmed,
            pending.ExpectedCipherId);
        return candidate.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("The pending-create entry is invalid.");
    }

    public static PersistedCipherCreatePendingEntry FromPending(
        CipherCreatePending pending) =>
        FromRecord(new CipherCreatePendingRecord(Guid.NewGuid(), 1, false, pending));

    public bool TryNormalize(out PersistedCipherCreatePendingEntry normalized)
    {
        normalized = null!;
        if (ReservationId == Guid.Empty
            || Revision <= 0
            || !IsRequiredIdentifier(AccountId, MaximumAccountIdLength)
            || !TryEndpoint(WebVault, out var webVault)
            || !TryEndpoint(Api, out var api)
            || !TryEndpoint(Identity, out var identity)
            || !TryEndpoint(Notifications, out var notifications)
            || !TryEndpoint(Icons, out var icons)
            || !TryEndpoint(SendWeb, out var sendWeb)
            || !Enum.IsDefined(typeof(Core.Enums.CipherType), CipherType)
            || !IsOptionalIdentifier(OrganizationId)
            || CollectionIds is null
            || BaselineCipherIds is null
            || CollectionIds.Count > MaximumCollectionIds
            || BaselineCipherIds.Count > MaximumBaselineCipherIds
            || !TryNormalizeIdentifiers(CollectionIds, out var collectionIds)
            || !TryNormalizeIdentifiers(BaselineCipherIds, out var baselineCipherIds)
            || !IsFingerprint(KeyFingerprint)
            || !IsOptionalIdentifier(ExpectedCipherId)
            || (ExpectedCipherId is not null && !CommitConfirmed))
        {
            return false;
        }

        normalized = new PersistedCipherCreatePendingEntry(
            ReservationId,
            Revision,
            IsInFlight,
            AccountId,
            webVault.Value,
            api.Value,
            identity.Value,
            notifications.Value,
            icons.Value,
            sendWeb.Value,
            CipherType,
            OrganizationId,
            collectionIds,
            baselineCipherIds,
            KeyFingerprint,
            CommitConfirmed,
            ExpectedCipherId);
        return true;
    }

    public ServerEndpoints ToEndpoints() => new(
        new ServerEndpoint(WebVault),
        new ServerEndpoint(Api),
        new ServerEndpoint(Identity),
        new ServerEndpoint(Notifications),
        new ServerEndpoint(Icons),
        new ServerEndpoint(SendWeb));

    public CipherCreatePending ToPending() => new(
        new CipherCreateWitness(
            AccountId,
            ToEndpoints(),
            (Core.Enums.CipherType)CipherType,
            OrganizationId,
            CollectionIds,
            BaselineCipherIds,
            KeyFingerprint),
        CommitConfirmed,
        ExpectedCipherId);

    public CipherCreatePendingRecord ToRecord() => new(
        ReservationId,
        Revision,
        IsInFlight,
        ToPending());

    private static bool TryEndpoint(string? value, out ServerEndpoint endpoint)
    {
        endpoint = default;
        return value is not null
            && value.Length <= MaximumEndpointLength
            && ServerEndpoint.TryParse(value, out endpoint);
    }

    private static bool TryNormalizeIdentifiers(
        IReadOnlyList<string> values,
        out IReadOnlyList<string> normalized)
    {
        normalized = [];
        var result = new List<string>(values.Count);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!IsRequiredIdentifier(value, MaximumIdentifierLength)
                || !distinct.Add(value))
            {
                return false;
            }
            result.Add(value);
        }
        normalized = result;
        return true;
    }

    private static bool IsOptionalIdentifier(string? value) =>
        value is null || IsRequiredIdentifier(value, MaximumIdentifierLength);

    private static bool IsRequiredIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 }
        && value.Length <= maximumLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));

    private static bool IsFingerprint(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }
}
