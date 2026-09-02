using System.Text.Json.Serialization;
using Core.Enums;

namespace Core.Models;

public sealed record PersistedSession
{
    public const int LegacySchemaVersion = 2;
    private const int EndpointSchemaVersion = 3;
    private const int MasterPasswordSaltSchemaVersion = 4;
    private const int KeyConnectorSchemaVersion = 5;
    private const int UnlockMethodSchemaVersion = 6;
    private const int AccountMetadataSchemaVersion = 7;
    public const int CurrentSchemaVersion = 8;
    public const long MaxPersistedGeneration = long.MaxValue - 1;

    [JsonConstructor]
    public PersistedSession(
        int schemaVersion,
        string accountId,
        ServerOrigin origin,
        ServerEndpoints? endpoints,
        long generation,
        string email,
        string deviceIdentifier,
        string refreshToken,
        string protectedUserKey,
        KdfType kdfType,
        int kdfIterations,
        int? kdfMemory,
        int? kdfParallelism,
        string? masterPasswordSalt = null,
        PersistedSessionUnlockMethod unlockMethod = PersistedSessionUnlockMethod.MasterPassword,
        string? keyConnectorUrl = null,
        int userKeySecurityVersion = 1,
        string? userKeyId = null,
        PersistedAccountCryptographicState? accountCryptographicState = null)
    {
        SchemaVersion = schemaVersion;
        AccountId = accountId;
        Origin = origin;
        Endpoints = endpoints;
        Generation = generation;
        Email = email;
        DeviceIdentifier = deviceIdentifier;
        RefreshToken = refreshToken;
        ProtectedUserKey = protectedUserKey;
        KdfType = kdfType;
        KdfIterations = kdfIterations;
        KdfMemory = kdfMemory;
        KdfParallelism = kdfParallelism;
        MasterPasswordSalt = masterPasswordSalt;
        UnlockMethod = unlockMethod;
        KeyConnectorUrl = keyConnectorUrl;
        UserKeySecurityVersion = userKeySecurityVersion;
        UserKeyId = userKeyId;
        AccountCryptographicState = accountCryptographicState;
    }

    public PersistedSession(
        int schemaVersion,
        string accountId,
        ServerEndpoints endpoints,
        long generation,
        string email,
        string deviceIdentifier,
        string refreshToken,
        string protectedUserKey,
        KdfType kdfType,
        int kdfIterations,
        int? kdfMemory,
        int? kdfParallelism,
        string? masterPasswordSalt = null,
        PersistedSessionUnlockMethod unlockMethod = PersistedSessionUnlockMethod.MasterPassword,
        string? keyConnectorUrl = null,
        int userKeySecurityVersion = 1,
        string? userKeyId = null,
        PersistedAccountCryptographicState? accountCryptographicState = null)
        : this(
            schemaVersion,
            accountId,
            endpoints?.WebVault.Origin ?? default,
            endpoints,
            generation,
            email,
            deviceIdentifier,
            refreshToken,
            protectedUserKey,
            kdfType,
            kdfIterations,
            kdfMemory,
            kdfParallelism,
            ResolveTrustedConstructorSalt(schemaVersion, email, masterPasswordSalt),
            unlockMethod,
            keyConnectorUrl,
            userKeySecurityVersion,
            userKeyId,
            accountCryptographicState)
    {
    }

    public PersistedSession(
        int SchemaVersion,
        string AccountId,
        ServerOrigin Origin,
        long Generation,
        string Email,
        string DeviceIdentifier,
        string RefreshToken,
        string ProtectedUserKey,
        KdfType KdfType,
        int KdfIterations,
        int? KdfMemory,
        int? KdfParallelism,
        string? MasterPasswordSalt = null,
        PersistedSessionUnlockMethod UnlockMethod = PersistedSessionUnlockMethod.MasterPassword,
        string? KeyConnectorUrl = null,
        int UserKeySecurityVersion = 1,
        string? UserKeyId = null,
        PersistedAccountCryptographicState? AccountCryptographicState = null)
        : this(
            SchemaVersion,
            AccountId,
            Origin,
            CreateCompatibilityEndpoints(SchemaVersion, Origin),
            Generation,
            Email,
            DeviceIdentifier,
            RefreshToken,
            ProtectedUserKey,
            KdfType,
            KdfIterations,
            KdfMemory,
            KdfParallelism,
            ResolveTrustedConstructorSalt(SchemaVersion, Email, MasterPasswordSalt),
            UnlockMethod,
            KeyConnectorUrl,
            UserKeySecurityVersion,
            UserKeyId,
            AccountCryptographicState)
    {
    }

    public int SchemaVersion { get; init; }
    public string AccountId { get; init; }
    public ServerOrigin Origin { get; init; }
    public ServerEndpoints? Endpoints { get; init; }
    public long Generation { get; init; }
    public string Email { get; init; }
    public string DeviceIdentifier { get; init; }
    public string RefreshToken { get; init; }
    public string ProtectedUserKey { get; init; }
    public KdfType KdfType { get; init; }
    public int KdfIterations { get; init; }
    public int? KdfMemory { get; init; }
    public int? KdfParallelism { get; init; }
    public string? MasterPasswordSalt { get; init; }
    public PersistedSessionUnlockMethod UnlockMethod { get; init; }
    public string? KeyConnectorUrl { get; init; }
    public bool? HasMasterPassword { get; init; }
    public bool SupportsDeviceTrust { get; init; }
    public int UserKeySecurityVersion { get; init; } = 1;
    public string? UserKeyId { get; init; }
    public PersistedAccountCryptographicState? AccountCryptographicState { get; init; }

    [JsonIgnore]
    public string EffectiveMasterPasswordSalt =>
        MasterPasswordSalt
        ?? throw new InvalidOperationException("Persisted session has no master-password salt.");

    public bool HasValidBinding =>
        SchemaVersion == CurrentSchemaVersion
        && HasValidLegacyIdentity()
        && HasValidEndpointBinding()
        && HasValidUnlockBinding()
        && HasValidAccountCryptographicBinding();

    public string ServerUrl => Endpoints?.WebVault.Value ?? string.Empty;

    public string? PinProtectedUserKey { get; init; }
    public int PinFailedAttempts { get; init; }
    public string? TwoFactorToken { get; init; }
    public string? HelloProtectedUserKey { get; init; }
    public string? HelloChallenge { get; init; }

    public bool TryMigrate(out PersistedSession migrated)
    {
        if (HasValidBinding)
        {
            migrated = this;
            return true;
        }

        if (!HasValidLegacyIdentity())
        {
            migrated = null!;
            return false;
        }

        try
        {
            if (SchemaVersion == LegacySchemaVersion && Endpoints is null)
            {
                migrated = this with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Endpoints = ServerEndpoints.FromWebVault(Origin.Value),
                    MasterPasswordSalt = MasterPasswordSaltPolicy.NormalizeEmail(Email),
                    UnlockMethod = PersistedSessionUnlockMethod.MasterPassword,
                    KeyConnectorUrl = null,
                    HasMasterPassword = null,
                    SupportsDeviceTrust = false,
                    UserKeySecurityVersion = 1,
                    UserKeyId = null,
                    AccountCryptographicState = null,
                };
                return migrated.HasValidBinding;
            }

            if (SchemaVersion == EndpointSchemaVersion && HasValidEndpointBinding())
            {
                migrated = this with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    MasterPasswordSalt = MasterPasswordSaltPolicy.NormalizeEmail(Email),
                    UnlockMethod = PersistedSessionUnlockMethod.MasterPassword,
                    KeyConnectorUrl = null,
                    HasMasterPassword = null,
                    SupportsDeviceTrust = false,
                    UserKeySecurityVersion = 1,
                    UserKeyId = null,
                    AccountCryptographicState = null,
                };
                return migrated.HasValidBinding;
            }

            if (SchemaVersion == MasterPasswordSaltSchemaVersion
                && HasValidEndpointBinding()
                && MasterPasswordSaltPolicy.IsValid(MasterPasswordSalt))
            {
                migrated = this with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    UnlockMethod = PersistedSessionUnlockMethod.MasterPassword,
                    KeyConnectorUrl = null,
                    HasMasterPassword = null,
                    SupportsDeviceTrust = false,
                    UserKeySecurityVersion = 1,
                    UserKeyId = null,
                    AccountCryptographicState = null,
                };
                return migrated.HasValidBinding;
            }

            if (SchemaVersion == KeyConnectorSchemaVersion
                && HasValidEndpointBinding()
                && MasterPasswordSaltPolicy.IsValid(MasterPasswordSalt))
            {
                migrated = this with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    UnlockMethod = string.IsNullOrWhiteSpace(KeyConnectorUrl)
                        ? PersistedSessionUnlockMethod.MasterPassword
                        : PersistedSessionUnlockMethod.KeyConnector,
                    HasMasterPassword = null,
                    SupportsDeviceTrust = false,
                    UserKeySecurityVersion = 1,
                    UserKeyId = null,
                    AccountCryptographicState = null,
                };
                return migrated.HasValidBinding;
            }

            if (SchemaVersion == UnlockMethodSchemaVersion
                && HasValidEndpointBinding()
                && HasValidUnlockBinding())
            {
                migrated = this with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    HasMasterPassword = null,
                    SupportsDeviceTrust = false,
                    UserKeySecurityVersion = 1,
                    UserKeyId = null,
                    AccountCryptographicState = null,
                };
                return migrated.HasValidBinding;
            }

            if (SchemaVersion == AccountMetadataSchemaVersion
                && HasValidEndpointBinding()
                && HasValidUnlockBinding())
            {
                migrated = this with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    SupportsDeviceTrust = false,
                    UserKeySecurityVersion = 1,
                    UserKeyId = null,
                    AccountCryptographicState = null,
                };
                return migrated.HasValidBinding;
            }

            migrated = null!;
            return false;
        }
        catch (ArgumentException)
        {
            migrated = null!;
            return false;
        }
    }

    private bool HasValidLegacyIdentity() =>
        !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(Email)
        && Generation > 0
        && Generation <= MaxPersistedGeneration
        && Origin.IsInitialized;

    private bool HasValidEndpointBinding() =>
        Endpoints is not null
        && Endpoints.WebVault.IsInitialized
        && Endpoints.Api.IsInitialized
        && Endpoints.Identity.IsInitialized
        && Endpoints.Notifications.IsInitialized
        && Endpoints.Icons.IsInitialized
        && Endpoints.SendWeb.IsInitialized
        && Origin == Endpoints.WebVault.Origin;

    private bool HasValidUnlockBinding()
    {
        if (!MasterPasswordSaltPolicy.IsValid(MasterPasswordSalt)
            || string.IsNullOrWhiteSpace(RefreshToken))
            return false;
        return UnlockMethod switch
        {
            PersistedSessionUnlockMethod.MasterPassword =>
                string.IsNullOrWhiteSpace(KeyConnectorUrl)
                && !string.IsNullOrWhiteSpace(ProtectedUserKey),
            PersistedSessionUnlockMethod.KeyConnector =>
                KeyConnectorEndpointPolicy.TryNormalize(KeyConnectorUrl, out var normalized)
                && string.Equals(normalized, KeyConnectorUrl, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(ProtectedUserKey),
            PersistedSessionUnlockMethod.TrustedDeviceSso =>
                string.IsNullOrWhiteSpace(KeyConnectorUrl)
                && !string.IsNullOrWhiteSpace(RefreshToken)
                && Guid.TryParseExact(DeviceIdentifier, "D", out _),
            _ => false,
        };
    }

    private bool HasValidAccountCryptographicBinding()
    {
        if (!HasCanonicalUserKeyId(UserKeyId))
            return false;

        return UserKeySecurityVersion switch
        {
            1 => AccountCryptographicState is null
                || AccountCryptographicState is
                {
                    SecurityVersion: 1,
                    HasValidShape: true,
                },
            2 => AccountCryptographicState is
            {
                SecurityVersion: 2,
                HasValidShape: true,
            },
            _ => false,
        };
    }

    private static bool HasCanonicalUserKeyId(string? value)
    {
        if (value is null)
            return true;
        if (value.Length != 32)
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

    private static string? ResolveTrustedConstructorSalt(
        int schemaVersion,
        string email,
        string? masterPasswordSalt) =>
        schemaVersion == CurrentSchemaVersion && masterPasswordSalt is null
            ? MasterPasswordSaltPolicy.NormalizeEmail(email)
            : masterPasswordSalt;

    private static ServerEndpoints? CreateCompatibilityEndpoints(
        int schemaVersion,
        ServerOrigin origin) =>
        schemaVersion is EndpointSchemaVersion
            or MasterPasswordSaltSchemaVersion
            or KeyConnectorSchemaVersion
            or UnlockMethodSchemaVersion
            or AccountMetadataSchemaVersion
            or CurrentSchemaVersion
            && origin.IsInitialized
            ? ServerEndpoints.FromWebVault(origin.Value)
            : null;
}
