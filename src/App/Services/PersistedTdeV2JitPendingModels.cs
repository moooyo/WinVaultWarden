using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedTdeV2JitPendingLedger(
    string Format,
    int SchemaVersion,
    IReadOnlyList<PersistedTdeV2JitPendingEntry> Entries)
{
    public const string FormatName = "WinVaultWarden.TdeV2JitPendingLedger";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 8;

    public static PersistedTdeV2JitPendingLedger Empty { get; } = new(
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

        var bindings = new HashSet<PendingBinding>();
        var entryIds = new HashSet<Guid>();
        var userKeyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (entry is null
                || !entry.TryValidate()
                || !bindings.Add(PendingBinding.From(entry))
                || !entryIds.Add(entry.EntryId)
                || !userKeyIds.Add(entry.UserKeyId))
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
            entry?.ClearSecrets();
    }

    private sealed record PendingBinding(
        string AccountId,
        ServerEndpoints Endpoints,
        string DeviceIdentifier)
    {
        public static PendingBinding From(PersistedTdeV2JitPendingEntry entry) => new(
            entry.AccountId,
            entry.ToEndpoints(),
            entry.DeviceIdentifier);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PersistedTdeV2JitPendingEntry(
    Guid EntryId,
    long Revision,
    int Stage,
    byte[] ProtectedUserKeyTransport,
    byte[] ProtectedDeviceKey,
    string AccountId,
    string WebVault,
    string Api,
    string Identity,
    string Notifications,
    string Icons,
    string SendWeb,
    string DeviceIdentifier,
    string OrganizationId,
    string OrganizationIdentifier,
    DateTimeOffset CreatedAt,
    string PublicKey,
    string EncryptedPrivateKey,
    string UserKeyEncryptedAccountPrivateKey,
    string AccountPublicKey,
    string PublicKeyEncryptionWrappedPrivateKey,
    string PublicKeyEncryptionPublicKey,
    string PublicKeyEncryptionSignedPublicKey,
    string SignatureAlgorithm,
    string WrappedSigningKey,
    string VerifyingKey,
    string SecurityState,
    int SecurityVersion,
    string UserKeyId,
    string ResetPasswordKey,
    bool TrustCurrentDevice,
    string? TrustedDeviceEncryptedUserKey,
    string? TrustedDeviceEncryptedPublicKey,
    string? TrustedDeviceEncryptedPrivateKey)
{
    public const int MaximumProtectedSecretLength = 16 * 1024;

    public static PersistedTdeV2JitPendingEntry Create(
        Guid entryId,
        long revision,
        TdeV2JitPendingStage stage,
        TdeV2JitPending pending,
        byte[] protectedUserKeyTransport,
        byte[]? protectedDeviceKey)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(protectedUserKeyTransport);
        var request = pending.AccountKeysRequest;
        var accountKeys = request.AccountKeys;
        var encryption = accountKeys.PublicKeyEncryptionKeyPair;
        var signature = accountKeys.SignatureKeyPair;
        var security = accountKeys.SecurityState;
        var trusted = pending.TrustedDeviceRequest;
        var entry = new PersistedTdeV2JitPendingEntry(
            entryId,
            revision,
            (int)stage,
            protectedUserKeyTransport,
            protectedDeviceKey ?? [],
            pending.AccountId,
            pending.Endpoints.WebVault.Value,
            pending.Endpoints.Api.Value,
            pending.Endpoints.Identity.Value,
            pending.Endpoints.Notifications.Value,
            pending.Endpoints.Icons.Value,
            pending.Endpoints.SendWeb.Value,
            pending.DeviceIdentifier,
            pending.OrganizationId,
            pending.OrganizationIdentifier,
            pending.CreatedAt,
            request.PublicKey,
            request.EncryptedPrivateKey,
            accountKeys.UserKeyEncryptedAccountPrivateKey,
            accountKeys.AccountPublicKey,
            encryption.WrappedPrivateKey,
            encryption.PublicKey,
            encryption.SignedPublicKey,
            signature.SignatureAlgorithm,
            signature.WrappedSigningKey,
            signature.VerifyingKey,
            security.SecurityState,
            security.SecurityVersion,
            request.UserKeyId,
            pending.ResetPasswordKey,
            pending.TrustCurrentDevice,
            trusted?.EncryptedUserKey,
            trusted?.EncryptedPublicKey,
            trusted?.EncryptedPrivateKey);
        return entry.TryValidate()
            ? entry
            : throw new InvalidDataException("The V2 JIT pending entry is invalid.");
    }

    public bool TryValidate()
    {
        if (EntryId == Guid.Empty
            || Revision <= 0
            || !Enum.IsDefined(typeof(TdeV2JitPendingStage), Stage)
            || ProtectedUserKeyTransport is null
            || ProtectedUserKeyTransport.Length is <= 0 or > MaximumProtectedSecretLength
            || ProtectedDeviceKey is null
            || TrustCurrentDevice && ProtectedDeviceKey.Length is <= 0 or > MaximumProtectedSecretLength
            || !TrustCurrentDevice && ProtectedDeviceKey.Length != 0
            || CreatedAt.Offset != TimeSpan.Zero
            || CreatedAt < DateTimeOffset.UnixEpoch
            || TrustCurrentDevice != (TrustedDeviceEncryptedUserKey is not null)
            || TrustCurrentDevice != (TrustedDeviceEncryptedPublicKey is not null)
            || TrustCurrentDevice != (TrustedDeviceEncryptedPrivateKey is not null)
            || !IsCanonicalGuid(AccountId)
            || !IsCanonicalGuid(DeviceIdentifier)
            || !IsCanonicalGuid(OrganizationId)
            || !IsIdentifier(
                OrganizationIdentifier,
                TdeV2JitPending.MaximumOrganizationIdentifierLength))
        {
            return false;
        }

        try
        {
            var endpoints = ToEndpoints();
            _ = new DeviceKeyBinding(AccountId, endpoints, DeviceIdentifier);
            _ = ToAccountKeysRequest();
            _ = ToTrustedDeviceRequest();
            return IsCanonicalEndpoint(WebVault, endpoints.WebVault.Value)
                && IsCanonicalEndpoint(Api, endpoints.Api.Value)
                && IsCanonicalEndpoint(Identity, endpoints.Identity.Value)
                && IsCanonicalEndpoint(Notifications, endpoints.Notifications.Value)
                && IsCanonicalEndpoint(Icons, endpoints.Icons.Value)
                && IsCanonicalEndpoint(SendWeb, endpoints.SendWeb.Value)
                && IsType4Rsa2048(ResetPasswordKey);
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    public TdeV2JitAccountKeysRequestSnapshot ToAccountKeysRequest() => new(
        PublicKey,
        EncryptedPrivateKey,
        new TdeV2JitAccountKeysSnapshot(
            UserKeyEncryptedAccountPrivateKey,
            AccountPublicKey,
            new TdeV2JitPublicKeyEncryptionKeyPairSnapshot(
                PublicKeyEncryptionWrappedPrivateKey,
                PublicKeyEncryptionPublicKey,
                PublicKeyEncryptionSignedPublicKey),
            new TdeV2JitSignatureKeyPairSnapshot(
                SignatureAlgorithm,
                WrappedSigningKey,
                VerifyingKey),
            new TdeV2JitAccountSecurityStateSnapshot(
                SecurityState,
                SecurityVersion)),
        UserKeyId);

    public TdeV2JitTrustedDeviceRequestSnapshot? ToTrustedDeviceRequest() =>
        TrustCurrentDevice
            ? new TdeV2JitTrustedDeviceRequestSnapshot(
                TrustedDeviceEncryptedUserKey!,
                TrustedDeviceEncryptedPublicKey!,
                TrustedDeviceEncryptedPrivateKey!)
            : null;

    public TdeV2JitPending ToPending(
        ReadOnlySpan<byte> userKeyTransport,
        ReadOnlySpan<byte> deviceKey) => new(
        new DeviceKeyBinding(AccountId, ToEndpoints(), DeviceIdentifier),
        OrganizationId,
        OrganizationIdentifier,
        CreatedAt,
        userKeyTransport,
        ToAccountKeysRequest(),
        ResetPasswordKey,
        ToTrustedDeviceRequest(),
        deviceKey);

    public TdeV2JitPendingStoreRecord ToRecord(
        ReadOnlySpan<byte> userKeyTransport,
        ReadOnlySpan<byte> deviceKey) => new(
        EntryId,
        Revision,
        (TdeV2JitPendingStage)Stage,
        ToPending(userKeyTransport, deviceKey));

    public ServerEndpoints ToEndpoints() => new(
        new ServerEndpoint(WebVault),
        new ServerEndpoint(Api),
        new ServerEndpoint(Identity),
        new ServerEndpoint(Notifications),
        new ServerEndpoint(Icons),
        new ServerEndpoint(SendWeb));

    public void ClearSecrets()
    {
        Clear(ProtectedDeviceKey);
        Clear(ProtectedUserKeyTransport);
    }

    private static bool IsCanonicalGuid(string? value) =>
        value is not null
        && Guid.TryParseExact(value, "D", out var parsed)
        && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    private static bool IsCanonicalEndpoint(string? value, string canonical) =>
        value is not null
        && value.Length <= TdeV2JitPending.MaximumEndpointLength
        && string.Equals(value, canonical, StringComparison.Ordinal);

    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is not null
        && value.Length is > 0
        && value.Length <= maximumLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && HasValidCharacters(value);

    private static bool HasValidCharacters(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character))
                return false;
            if (!char.IsSurrogate(character))
                continue;
            if (!char.IsHighSurrogate(character)
                || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }
            index++;
        }
        return true;
    }

    private static bool IsType4Rsa2048(string? value)
    {
        if (value is null
            || value.Length is <= 2 or > TdeV2JitPending.MaximumWireValueLength
            || !value.StartsWith("4.", StringComparison.Ordinal))
        {
            return false;
        }
        byte[]? decoded = null;
        try
        {
            decoded = Convert.FromBase64String(value[2..]);
            return decoded.Length == 256
                && string.Equals(
                    Convert.ToBase64String(decoded),
                    value[2..],
                    StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            Clear(decoded);
        }
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
}
