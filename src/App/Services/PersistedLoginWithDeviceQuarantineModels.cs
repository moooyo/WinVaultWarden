using System.Text.Json.Serialization;
using Core.Models;

namespace App.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedLoginWithDeviceQuarantineLedger(
    string Format,
    int SchemaVersion,
    IReadOnlyList<PersistedLoginWithDeviceQuarantineEntry> Entries)
{
    public const string FormatName = "WinVaultWarden.LoginWithDeviceQuarantineLedger";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 32;

    public static PersistedLoginWithDeviceQuarantineLedger Empty { get; } = new(
        FormatName,
        CurrentSchemaVersion,
        []);

    public bool TryValidate()
    {
        if (!string.Equals(Format, FormatName, StringComparison.Ordinal)
            || SchemaVersion != CurrentSchemaVersion
            || Entries is null
            || Entries.Count > MaximumEntries)
        {
            return false;
        }

        var entryIds = new HashSet<Guid>();
        var bindings = new HashSet<QuarantineBinding>();
        foreach (var entry in Entries)
        {
            if (entry is null
                || !entry.TryValidate()
                || !entryIds.Add(entry.EntryId)
                || !bindings.Add(QuarantineBinding.From(entry)))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record QuarantineBinding(
        ServerEndpoints Endpoints,
        string NormalizedEmail,
        string DeviceIdentifier)
    {
        public static QuarantineBinding From(
            PersistedLoginWithDeviceQuarantineEntry entry) => new(
            entry.ToEndpoints(),
            entry.NormalizedEmail,
            entry.DeviceIdentifier);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedLoginWithDeviceQuarantineEntry(
    Guid EntryId,
    long Revision,
    string WebVault,
    string Api,
    string Identity,
    string Notifications,
    string Icons,
    string SendWeb,
    string NormalizedEmail,
    string DeviceIdentifier,
    string RequestId,
    DateTimeOffset ArmedAt,
    DateTimeOffset RequestExpiresAt,
    DateTimeOffset ClearEligibleAt)
{
    public static PersistedLoginWithDeviceQuarantineEntry From(
        LoginWithDeviceQuarantineRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var endpoints = record.Binding.Endpoints;
        var entry = new PersistedLoginWithDeviceQuarantineEntry(
            record.EntryId,
            record.Revision,
            endpoints.WebVault.Value,
            endpoints.Api.Value,
            endpoints.Identity.Value,
            endpoints.Notifications.Value,
            endpoints.Icons.Value,
            endpoints.SendWeb.Value,
            record.Binding.NormalizedEmail,
            record.Binding.DeviceIdentifier,
            record.RequestId,
            record.ArmedAt,
            record.RequestExpiresAt,
            record.ClearEligibleAt);
        return entry.TryValidate()
            ? entry
            : throw new InvalidDataException("The device-login quarantine entry is invalid.");
    }

    public bool TryValidate()
    {
        if (EntryId == Guid.Empty
            || Revision <= 0
            || !IsCanonicalNonEmptyGuid(DeviceIdentifier)
            || !IsCanonicalNonEmptyGuid(RequestId)
            || !MasterPasswordSaltPolicy.IsValid(NormalizedEmail)
            || !string.Equals(
                NormalizedEmail,
                MasterPasswordSaltPolicy.NormalizeEmail(NormalizedEmail),
                StringComparison.Ordinal)
            || ArmedAt.Offset != TimeSpan.Zero
            || RequestExpiresAt.Offset != TimeSpan.Zero
            || ClearEligibleAt.Offset != TimeSpan.Zero
            || ArmedAt < DateTimeOffset.UnixEpoch
            || RequestExpiresAt <= ArmedAt
            || ClearEligibleAt < RequestExpiresAt)
        {
            return false;
        }

        try
        {
            var endpoints = ToEndpoints();
            var binding = new LoginWithDeviceQuarantineBinding(
                endpoints,
                NormalizedEmail,
                DeviceIdentifier);
            var record = new LoginWithDeviceQuarantineRecord(
                EntryId,
                Revision,
                binding,
                RequestId,
                ArmedAt,
                RequestExpiresAt,
                ClearEligibleAt);
            return IsCanonicalEndpoint(WebVault, endpoints.WebVault.Value)
                && IsCanonicalEndpoint(Api, endpoints.Api.Value)
                && IsCanonicalEndpoint(Identity, endpoints.Identity.Value)
                && IsCanonicalEndpoint(Notifications, endpoints.Notifications.Value)
                && IsCanonicalEndpoint(Icons, endpoints.Icons.Value)
                && IsCanonicalEndpoint(SendWeb, endpoints.SendWeb.Value)
                && string.Equals(
                    record.Binding.NormalizedEmail,
                    NormalizedEmail,
                    StringComparison.Ordinal)
                && string.Equals(
                    record.Binding.DeviceIdentifier,
                    DeviceIdentifier,
                    StringComparison.Ordinal)
                && string.Equals(record.RequestId, RequestId, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public LoginWithDeviceQuarantineRecord ToRecord() => new(
        EntryId,
        Revision,
        new LoginWithDeviceQuarantineBinding(
            ToEndpoints(),
            NormalizedEmail,
            DeviceIdentifier),
        RequestId,
        ArmedAt,
        RequestExpiresAt,
        ClearEligibleAt);

    public ServerEndpoints ToEndpoints() => new(
        new ServerEndpoint(WebVault),
        new ServerEndpoint(Api),
        new ServerEndpoint(Identity),
        new ServerEndpoint(Notifications),
        new ServerEndpoint(Icons),
        new ServerEndpoint(SendWeb));

    private static bool IsCanonicalNonEmptyGuid(string? value) =>
        value is not null
        && Guid.TryParseExact(value, "D", out var parsed)
        && parsed != Guid.Empty
        && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    private static bool IsCanonicalEndpoint(string? value, string canonical) =>
        value is not null
        && value.Length <= LoginWithDeviceQuarantineBinding.MaximumEndpointLength
        && string.Equals(value, canonical, StringComparison.Ordinal);
}
