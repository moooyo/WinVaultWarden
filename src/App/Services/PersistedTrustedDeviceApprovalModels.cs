using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedTrustedDeviceApprovalLedger(
    string Format,
    int SchemaVersion,
    IReadOnlyList<PersistedTrustedDeviceApprovalEntry> Entries)
{
    public const string FormatName = "WinVaultWarden.TrustedDeviceApprovalLedger";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 16;

    public static PersistedTrustedDeviceApprovalLedger Empty { get; } = new(
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

        var bindings = new HashSet<ApprovalBinding>();
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (entry is null
                || !entry.TryValidate()
                || !bindings.Add(ApprovalBinding.From(entry))
                || !requestIds.Add(entry.RequestId))
            {
                return false;
            }
        }
        return true;
    }

    public void ClearSecrets()
    {
        if (Entries is null)
            return;
        foreach (var entry in Entries)
            entry?.ClearSecret();
    }

    private sealed record ApprovalBinding(
        string AccountId,
        ServerEndpoints Endpoints,
        string DeviceIdentifier)
    {
        public static ApprovalBinding From(PersistedTrustedDeviceApprovalEntry entry) => new(
            entry.AccountId,
            entry.ToEndpoints(),
            entry.DeviceIdentifier);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedTrustedDeviceApprovalEntry(
    Guid EntryId,
    long Revision,
    string RequestId,
    byte[] ProtectedPrivateKey,
    string AccountId,
    string WebVault,
    string Api,
    string Identity,
    string Notifications,
    string Icons,
    string SendWeb,
    string DeviceIdentifier,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int RequestType)
{
    public const int MaximumProtectedPrivateKeyLength = 16 * 1024;

    public static PersistedTrustedDeviceApprovalEntry Create(
        Guid entryId,
        long revision,
        TrustedDeviceApprovalPending pending,
        byte[] protectedPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(protectedPrivateKey);
        var entry = new PersistedTrustedDeviceApprovalEntry(
            entryId,
            revision,
            pending.RequestId,
            protectedPrivateKey,
            pending.AccountId,
            pending.Endpoints.WebVault.Value,
            pending.Endpoints.Api.Value,
            pending.Endpoints.Identity.Value,
            pending.Endpoints.Notifications.Value,
            pending.Endpoints.Icons.Value,
            pending.Endpoints.SendWeb.Value,
            pending.DeviceIdentifier,
            pending.CreatedAt,
            pending.ExpiresAt,
            (int)pending.RequestType);
        return entry.TryValidate()
            ? entry
            : throw new InvalidDataException("The approval entry is invalid.");
    }

    public bool TryValidate()
    {
        if (EntryId == Guid.Empty
            || Revision <= 0
            || ProtectedPrivateKey is null
            || ProtectedPrivateKey.Length is <= 0 or > MaximumProtectedPrivateKeyLength
            || !Guid.TryParseExact(RequestId, "D", out var requestId)
            || !string.Equals(RequestId, requestId.ToString("D"), StringComparison.Ordinal)
            || !Guid.TryParseExact(DeviceIdentifier, "D", out var deviceId)
            || !string.Equals(
                DeviceIdentifier,
                deviceId.ToString("D"),
                StringComparison.Ordinal)
            || !Enum.IsDefined(typeof(TrustedDeviceApprovalRequestType), RequestType)
            || CreatedAt.Offset != TimeSpan.Zero
            || ExpiresAt.Offset != TimeSpan.Zero
            || CreatedAt < DateTimeOffset.UnixEpoch
            || ExpiresAt <= CreatedAt
            || ExpiresAt - CreatedAt > TrustedDeviceApprovalPending.MaximumLifetime)
        {
            return false;
        }

        try
        {
            var endpoints = ToEndpoints();
            _ = new DeviceKeyBinding(AccountId, endpoints, DeviceIdentifier);
            return WebVault.Length <= TrustedDeviceApprovalPending.MaximumEndpointLength
                && Api.Length <= TrustedDeviceApprovalPending.MaximumEndpointLength
                && Identity.Length <= TrustedDeviceApprovalPending.MaximumEndpointLength
                && Notifications.Length <= TrustedDeviceApprovalPending.MaximumEndpointLength
                && Icons.Length <= TrustedDeviceApprovalPending.MaximumEndpointLength
                && SendWeb.Length <= TrustedDeviceApprovalPending.MaximumEndpointLength
                && string.Equals(WebVault, endpoints.WebVault.Value, StringComparison.Ordinal)
                && string.Equals(Api, endpoints.Api.Value, StringComparison.Ordinal)
                && string.Equals(Identity, endpoints.Identity.Value, StringComparison.Ordinal)
                && string.Equals(
                    Notifications,
                    endpoints.Notifications.Value,
                    StringComparison.Ordinal)
                && string.Equals(Icons, endpoints.Icons.Value, StringComparison.Ordinal)
                && string.Equals(SendWeb, endpoints.SendWeb.Value, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public TrustedDeviceApprovalPending ToPending(ReadOnlySpan<byte> privateKeyPkcs8) => new(
        RequestId,
        privateKeyPkcs8,
        AccountId,
        ToEndpoints(),
        DeviceIdentifier,
        CreatedAt,
        ExpiresAt,
        (TrustedDeviceApprovalRequestType)RequestType);

    public TrustedDeviceApprovalStoreRecord ToRecord(ReadOnlySpan<byte> privateKeyPkcs8) => new(
        EntryId,
        Revision,
        ToPending(privateKeyPkcs8));

    public ServerEndpoints ToEndpoints() => new(
        new ServerEndpoint(WebVault),
        new ServerEndpoint(Api),
        new ServerEndpoint(Identity),
        new ServerEndpoint(Notifications),
        new ServerEndpoint(Icons),
        new ServerEndpoint(SendWeb));

    public void ClearSecret()
    {
        if (ProtectedPrivateKey is not null)
            CryptographicOperations.ZeroMemory(ProtectedPrivateKey);
    }
}
