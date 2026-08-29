using System.Text.Json.Serialization;

namespace Core.Models;

/// <summary>
/// Stores an encrypted sync payload together with its complete session binding.
/// </summary>
public sealed record VaultCacheEntry
{
    public const int LegacySchemaVersion = 2;
    public const int CurrentSchemaVersion = 3;

    [JsonConstructor]
    public VaultCacheEntry(
        int schemaVersion,
        string accountId,
        ServerOrigin origin,
        ServerEndpoints? endpoints,
        long generation,
        string email,
        DateTimeOffset cachedAtUtc,
        string syncResponseJson)
    {
        SchemaVersion = schemaVersion;
        AccountId = accountId;
        Origin = origin;
        Endpoints = endpoints;
        Generation = generation;
        Email = email;
        CachedAtUtc = cachedAtUtc;
        SyncResponseJson = syncResponseJson;
    }

    public VaultCacheEntry(
        int schemaVersion,
        string accountId,
        ServerEndpoints endpoints,
        long generation,
        string email,
        DateTimeOffset cachedAtUtc,
        string syncResponseJson)
        : this(
            schemaVersion,
            accountId,
            endpoints?.WebVault.Origin ?? default,
            endpoints,
            generation,
            email,
            cachedAtUtc,
            syncResponseJson)
    {
    }

    public VaultCacheEntry(
        int schemaVersion,
        string accountId,
        ServerOrigin origin,
        long generation,
        string email,
        DateTimeOffset cachedAtUtc,
        string syncResponseJson)
        : this(
            schemaVersion,
            accountId,
            origin,
            schemaVersion == CurrentSchemaVersion && origin.IsInitialized
                ? ServerEndpoints.FromWebVault(origin.Value)
                : null,
            generation,
            email,
            cachedAtUtc,
            syncResponseJson)
    {
    }

    public int SchemaVersion { get; init; }
    public string AccountId { get; init; }
    public ServerOrigin Origin { get; init; }
    public ServerEndpoints? Endpoints { get; init; }
    public long Generation { get; init; }
    public string Email { get; init; }
    public DateTimeOffset CachedAtUtc { get; init; }
    public string SyncResponseJson { get; init; }
    public string ServerUrl => Endpoints?.WebVault.Value ?? string.Empty;

    public bool HasValidBinding =>
        SchemaVersion == CurrentSchemaVersion
        && HasValidLegacyIdentity()
        && Endpoints is not null
        && Origin == Endpoints.WebVault.Origin;

    public bool TryMigrate(out VaultCacheEntry migrated)
    {
        if (HasValidBinding)
        {
            migrated = this;
            return true;
        }

        if (SchemaVersion != LegacySchemaVersion
            || Endpoints is not null
            || !HasValidLegacyIdentity())
        {
            migrated = null!;
            return false;
        }

        try
        {
            migrated = this with
            {
                SchemaVersion = CurrentSchemaVersion,
                Endpoints = ServerEndpoints.FromWebVault(Origin.Value),
            };
            return migrated.HasValidBinding;
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
        && Origin.IsInitialized;
}
