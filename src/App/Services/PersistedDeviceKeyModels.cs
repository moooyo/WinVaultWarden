using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Core.Models;

namespace App.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedDeviceKeyLedger(
    string Format,
    int SchemaVersion,
    IReadOnlyList<PersistedDeviceKeyEntry> Entries)
{
    public const string FormatName = "WinVaultWarden.DeviceKeyLedger";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 32;

    public static PersistedDeviceKeyLedger Empty { get; } = new(
        FormatName,
        CurrentSchemaVersion,
        []);

    public bool TryNormalize(out PersistedDeviceKeyLedger normalized)
    {
        normalized = null!;
        if (!string.Equals(Format, FormatName, StringComparison.Ordinal)
            || SchemaVersion != CurrentSchemaVersion
            || Entries is null
            || Entries.Count > MaximumEntries)
        {
            return false;
        }

        var entries = new List<PersistedDeviceKeyEntry>(Entries.Count);
        var bindings = new HashSet<DeviceKeyBindingKey>();
        foreach (var candidate in Entries)
        {
            if (candidate is null
                || !candidate.TryNormalize(out var entry)
                || !bindings.Add(DeviceKeyBindingKey.From(entry)))
            {
                return false;
            }
            entries.Add(entry);
        }

        normalized = new PersistedDeviceKeyLedger(
            FormatName,
            CurrentSchemaVersion,
            entries);
        return true;
    }

    public void ClearSecrets()
    {
        if (Entries is null)
            return;
        foreach (var entry in Entries)
            entry?.ClearSecret();
    }

    private sealed record DeviceKeyBindingKey(
        string AccountId,
        string WebVault,
        string Api,
        string Identity,
        string Notifications,
        string Icons,
        string SendWeb,
        string DeviceIdentifier)
    {
        public static DeviceKeyBindingKey From(PersistedDeviceKeyEntry entry) => new(
            entry.AccountId,
            entry.WebVault,
            entry.Api,
            entry.Identity,
            entry.Notifications,
            entry.Icons,
            entry.SendWeb,
            entry.DeviceIdentifier);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedDeviceKeyEntry(
    string AccountId,
    string WebVault,
    string Api,
    string Identity,
    string Notifications,
    string Icons,
    string SendWeb,
    string DeviceIdentifier,
    byte[] ProtectedDeviceKey)
{
    public const int MaximumEndpointLength = 2048;
    public const int MaximumProtectedDeviceKeyLength = 16 * 1024;

    public static PersistedDeviceKeyEntry Create(
        DeviceKeyBinding binding,
        byte[] protectedDeviceKey)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(protectedDeviceKey);
        var endpoints = binding.Endpoints;
        var candidate = new PersistedDeviceKeyEntry(
            binding.AccountId,
            endpoints.WebVault.Value,
            endpoints.Api.Value,
            endpoints.Identity.Value,
            endpoints.Notifications.Value,
            endpoints.Icons.Value,
            endpoints.SendWeb.Value,
            binding.DeviceIdentifier,
            protectedDeviceKey);
        return candidate.TryNormalize(out var normalized)
            ? normalized
            : throw new InvalidDataException("The device-key ledger entry is invalid.");
    }

    public bool TryNormalize(out PersistedDeviceKeyEntry normalized)
    {
        normalized = null!;
        if (ProtectedDeviceKey is null
            || ProtectedDeviceKey.Length is <= 0 or > MaximumProtectedDeviceKeyLength)
        {
            return false;
        }

        try
        {
            var endpoints = ToEndpoints();
            var binding = new DeviceKeyBinding(AccountId, endpoints, DeviceIdentifier);
            if (!HasCanonicalFields(binding))
                return false;
            normalized = new PersistedDeviceKeyEntry(
                binding.AccountId,
                endpoints.WebVault.Value,
                endpoints.Api.Value,
                endpoints.Identity.Value,
                endpoints.Notifications.Value,
                endpoints.Icons.Value,
                endpoints.SendWeb.Value,
                binding.DeviceIdentifier,
                ProtectedDeviceKey);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public DeviceKeyBinding ToBinding() => new(
        AccountId,
        ToEndpoints(),
        DeviceIdentifier);

    public ServerEndpoints ToEndpoints() => new(
        new ServerEndpoint(WebVault),
        new ServerEndpoint(Api),
        new ServerEndpoint(Identity),
        new ServerEndpoint(Notifications),
        new ServerEndpoint(Icons),
        new ServerEndpoint(SendWeb));

    public void ClearSecret()
    {
        if (ProtectedDeviceKey is not null)
            CryptographicOperations.ZeroMemory(ProtectedDeviceKey);
    }

    private bool HasCanonicalFields(DeviceKeyBinding binding)
    {
        var endpoints = binding.Endpoints;
        return string.Equals(AccountId, binding.AccountId, StringComparison.Ordinal)
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
        && value.Length <= MaximumEndpointLength
        && string.Equals(value, canonical, StringComparison.Ordinal);
}
