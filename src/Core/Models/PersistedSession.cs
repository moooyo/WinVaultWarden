using System.Text.Json.Serialization;
using Core.Enums;

namespace Core.Models;

public sealed record PersistedSession
{
    public const int LegacySchemaVersion = 2;
    private const int EndpointSchemaVersion = 3;
    public const int CurrentSchemaVersion = 4;
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
        string? masterPasswordSalt = null)
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
        string? masterPasswordSalt = null)
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
            ResolveTrustedConstructorSalt(schemaVersion, email, masterPasswordSalt))
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
        string? MasterPasswordSalt = null)
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
            ResolveTrustedConstructorSalt(SchemaVersion, Email, MasterPasswordSalt))
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

    [JsonIgnore]
    public string EffectiveMasterPasswordSalt =>
        MasterPasswordSalt
        ?? throw new InvalidOperationException("Persisted session has no master-password salt.");

    public bool HasValidBinding =>
        SchemaVersion == CurrentSchemaVersion
        && HasValidLegacyIdentity()
        && HasValidEndpointBinding()
        && MasterPasswordSaltPolicy.IsValid(MasterPasswordSalt);

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
                };
                return migrated.HasValidBinding;
            }

            if (SchemaVersion == EndpointSchemaVersion && HasValidEndpointBinding())
            {
                migrated = this with
                {
                    SchemaVersion = CurrentSchemaVersion,
                    MasterPasswordSalt = MasterPasswordSaltPolicy.NormalizeEmail(Email),
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
        schemaVersion is EndpointSchemaVersion or CurrentSchemaVersion
            && origin.IsInitialized
            ? ServerEndpoints.FromWebVault(origin.Value)
            : null;
}
