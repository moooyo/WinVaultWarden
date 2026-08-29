using Core.Enums;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class PersistedSessionEndpointTests
{
    [Fact]
    public void SchemaTwo_MigratesExplicitlyToSelfHostedEndpoints()
    {
        var legacy = Create(
            PersistedSession.LegacySchemaVersion,
            new ServerOrigin("https://vault.example/warden"),
            " Mixed@Example.COM ");

        Assert.Null(legacy.Endpoints);
        Assert.True(legacy.TryMigrate(out var migrated));
        Assert.Equal(PersistedSession.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal("https://vault.example/api", migrated.Endpoints!.Api.Value);
        Assert.Equal("mixed@example.com", migrated.MasterPasswordSalt);
        Assert.True(migrated.HasValidBinding);
    }

    [Fact]
    public void SchemaTwo_OfficialVaultOrigin_MigratesToOfficialCatalog()
    {
        var legacy = Create(
            PersistedSession.LegacySchemaVersion,
            new ServerOrigin("https://vault.bitwarden.com"));

        Assert.True(legacy.TryMigrate(out var migrated));
        Assert.Equal(ServerEndpoints.BitwardenUs, migrated.Endpoints);
        Assert.Equal("https://identity.bitwarden.com", migrated.Endpoints!.Identity.Value);
    }

    [Fact]
    public void SchemaFourWithoutEndpoints_IsRejectedInsteadOfInferred()
    {
        var invalid = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account",
            new ServerOrigin("https://vault.example"),
            endpoints: null,
            generation: 1,
            email: "me@example.com",
            deviceIdentifier: "device",
            refreshToken: "refresh",
            protectedUserKey: "2.key",
            kdfType: KdfType.Pbkdf2,
            kdfIterations: 600_000,
            kdfMemory: null,
            kdfParallelism: null);

        Assert.False(invalid.HasValidBinding);
        Assert.False(invalid.TryMigrate(out _));
    }

    [Fact]
    public void SchemaThree_MigratesWithNormalizedEmailSalt()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var legacy = new PersistedSession(
            schemaVersion: 3,
            accountId: "account",
            origin: endpoints.WebVault.Origin,
            endpoints,
            generation: 1,
            email: " Mixed@Example.COM ",
            deviceIdentifier: "device",
            refreshToken: "refresh",
            protectedUserKey: "2.key",
            kdfType: KdfType.Pbkdf2,
            kdfIterations: 600_000,
            kdfMemory: null,
            kdfParallelism: null,
            masterPasswordSalt: null);

        Assert.True(legacy.TryMigrate(out var migrated));
        Assert.Equal(PersistedSession.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal("mixed@example.com", migrated.MasterPasswordSalt);
        Assert.True(migrated.HasValidBinding);
    }

    [Fact]
    public void SchemaFourMissingSalt_IsRejected()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var invalid = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account",
            endpoints.WebVault.Origin,
            endpoints,
            1,
            "me@example.com",
            "device",
            "refresh",
            "2.key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            masterPasswordSalt: null);

        Assert.False(invalid.HasValidBinding);
        Assert.False(invalid.TryMigrate(out _));
    }

    [Fact]
    public void TrustedSchemaFourConstructor_FillsNormalizedEmailSalt()
    {
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account",
            ServerEndpoints.ForSelfHosted("https://vault.example"),
            1,
            " Mixed@Example.COM ",
            "device",
            "refresh",
            "2.key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);

        Assert.Equal("mixed@example.com", persisted.MasterPasswordSalt);
        Assert.True(persisted.HasValidBinding);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SchemaFourEmptyOrWhitespaceSalt_IsRejected(string salt)
    {
        var persisted = CreateCurrentWithExplicitSalt(salt);

        Assert.False(persisted.HasValidBinding);
        Assert.False(persisted.TryMigrate(out _));
    }

    [Fact]
    public void SchemaFourSaltOverProtocolLimit_IsRejected()
    {
        var persisted = CreateCurrentWithExplicitSalt(new string('s', 257));

        Assert.False(persisted.HasValidBinding);
        Assert.False(persisted.TryMigrate(out _));
    }

    [Fact]
    public void SchemaOne_RemainsRejected()
    {
        var legacy = Create(1, new ServerOrigin("https://vault.example"));

        Assert.False(legacy.TryMigrate(out _));
    }

    [Fact]
    public void Restore_SameAuthorityDifferentBasePath_RotatesLease()
    {
        var session = new VaultSession();
        var firstEndpoints = ServerEndpoints.ForSelfHosted("https://vault.example/one");
        var secondEndpoints = ServerEndpoints.ForSelfHosted("https://vault.example/two");
        var first = session.BeginAccountSession("account", "me@example.com", firstEndpoints);
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account",
            secondEndpoints,
            first.Generation,
            "me@example.com",
            "device",
            "refresh",
            "2.key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);

        var restored = session.RestorePersistedSession(persisted);

        Assert.False(session.IsCurrent(first));
        Assert.Equal(secondEndpoints, restored.Endpoints);
        Assert.True(restored.Generation > first.Generation);
    }

    private static PersistedSession CreateCurrentWithExplicitSalt(string salt)
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        return new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account",
            endpoints,
            1,
            "me@example.com",
            "device",
            "refresh",
            "2.key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            salt);
    }

    private static PersistedSession Create(
        int schemaVersion,
        ServerOrigin origin,
        string email = "me@example.com") => new(
        schemaVersion,
        "account",
        origin,
        1,
        email,
        "device",
        "refresh",
        "2.key",
        KdfType.Pbkdf2,
        600_000,
        null,
        null);
}
