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
    public void SchemaSixMigrationLeavesMasterPasswordMetadataUnknown()
    {
        var legacy = new PersistedSession(
            schemaVersion: 6,
            accountId: "account",
            endpoints: ServerEndpoints.ForSelfHosted("https://vault.example"),
            generation: 1,
            email: "me@example.com",
            deviceIdentifier: "device",
            refreshToken: "refresh",
            protectedUserKey: "2.key",
            kdfType: KdfType.Pbkdf2,
            kdfIterations: 600_000,
            kdfMemory: null,
            kdfParallelism: null,
            masterPasswordSalt: "me@example.com",
            unlockMethod: PersistedSessionUnlockMethod.KeyConnector,
            keyConnectorUrl: "https://keys.example")
        {
            HasMasterPassword = true,
        };

        Assert.True(legacy.TryMigrate(out var migrated));
        Assert.Equal(PersistedSession.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(PersistedSessionUnlockMethod.KeyConnector, migrated.UnlockMethod);
        Assert.Null(migrated.HasMasterPassword);
        Assert.True(migrated.HasValidBinding);
    }

    [Fact]
    public void SchemaFiveKeyConnectorUrlRestoresKeyConnectorUnlockMethod()
    {
        var legacy = new PersistedSession(
            schemaVersion: 5,
            accountId: "account",
            endpoints: ServerEndpoints.ForSelfHosted("https://vault.example"),
            generation: 1,
            email: "me@example.com",
            deviceIdentifier: "device",
            refreshToken: "refresh",
            protectedUserKey: "2.key",
            kdfType: KdfType.Pbkdf2,
            kdfIterations: 600_000,
            kdfMemory: null,
            kdfParallelism: null,
            masterPasswordSalt: "me@example.com",
            keyConnectorUrl: "https://keys.example");

        Assert.True(legacy.TryMigrate(out var migrated));
        Assert.Equal(PersistedSessionUnlockMethod.KeyConnector, migrated.UnlockMethod);
        Assert.Equal("https://keys.example", migrated.KeyConnectorUrl);
        Assert.True(migrated.HasValidBinding);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void LegacySchemas_MigrateOnlyAsV1AndClearCryptographicState(int schemaVersion)
    {
        var legacy = CreateLegacyWithInjectedV2State(schemaVersion);

        Assert.True(legacy.TryMigrate(out var migrated));
        Assert.Equal(PersistedSession.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Equal(1, migrated.UserKeySecurityVersion);
        Assert.Null(migrated.UserKeyId);
        Assert.Null(migrated.AccountCryptographicState);
        Assert.False(migrated.SupportsDeviceTrust);
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
    public void UnknownSchemaVersion_RemainsRejected()
    {
        var persisted = CreateCurrentWithExplicitSalt("me@example.com") with
        {
            SchemaVersion = PersistedSession.CurrentSchemaVersion + 1,
        };

        Assert.False(persisted.HasValidBinding);
        Assert.False(persisted.TryMigrate(out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void CurrentSchema_RejectsUnknownUserKeySecurityVersion(int securityVersion)
    {
        var persisted = CreateCurrentWithExplicitSalt("me@example.com") with
        {
            UserKeySecurityVersion = securityVersion,
        };

        Assert.False(persisted.HasValidBinding);
    }

    [Fact]
    public void CurrentSchema_V2RequiresMatchingAccountCryptographicState()
    {
        var persisted = CreateCurrentWithExplicitSalt("me@example.com") with
        {
            UserKeySecurityVersion = 2,
            UserKeyId = "00112233445566778899aabbccddeeff",
        };

        Assert.False(persisted.HasValidBinding);
        Assert.False((persisted with
        {
            UserKeySecurityVersion = 1,
            AccountCryptographicState = CreateV2AccountCryptographicState(),
        }).HasValidBinding);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00112233445566778899AABBCCDDEEFF")]
    [InlineData("00112233445566778899aabbccddeef")]
    [InlineData("00112233445566778899aabbccddeezz")]
    public void CurrentSchema_RejectsNonCanonicalUserKeyId(string userKeyId)
    {
        var persisted = CreateCurrentWithExplicitSalt("me@example.com") with
        {
            UserKeyId = userKeyId,
        };

        Assert.False(persisted.HasValidBinding);
    }

    [Fact]
    public void AccountCryptographicState_HasStrictBoundedShape()
    {
        Assert.True(new PersistedAccountCryptographicState(1, "2.wrapped").HasValidShape);
        Assert.False(new PersistedAccountCryptographicState(
            1,
            "2.wrapped",
            PublicKey: "unexpected").HasValidShape);
        Assert.True(new PersistedAccountCryptographicState(
            2,
            "7.wrapped",
            WrappedSigningKey: "7.signing",
            SignedSecurityState: "signed-state").HasValidShape);
        Assert.True(new PersistedAccountCryptographicState(
            2,
            "7.wrapped",
            PublicKey: "public",
            SignedPublicKey: "signed-public",
            WrappedSigningKey: "7.signing",
            VerifyingKey: "verifying",
            SignedSecurityState: "signed-state").HasValidShape);
        Assert.False(new PersistedAccountCryptographicState(
            2,
            "7.wrapped",
            PublicKey: string.Empty,
            WrappedSigningKey: "7.signing",
            SignedSecurityState: "signed-state").HasValidShape);
        Assert.False(new PersistedAccountCryptographicState(
            2,
            "7.wrapped",
            SignedPublicKey: new string(
                'x',
                PersistedAccountCryptographicState.MaximumEncodedValueLength + 1),
            WrappedSigningKey: "7.signing",
            SignedSecurityState: "signed-state").HasValidShape);
        Assert.False(new PersistedAccountCryptographicState(
            2,
            "7.wrapped",
            SignedSecurityState: "signed-state").HasValidShape);
        Assert.False(new PersistedAccountCryptographicState(
            1,
            new string('x', PersistedAccountCryptographicState.MaximumEncodedValueLength + 1))
            .HasValidShape);
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

    private static PersistedSession CreateLegacyWithInjectedV2State(int schemaVersion)
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        return new PersistedSession(
            schemaVersion,
            "account",
            endpoints.WebVault.Origin,
            schemaVersion == PersistedSession.LegacySchemaVersion ? null : endpoints,
            1,
            "me@example.com",
            "device",
            "refresh",
            "2.key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            schemaVersion >= 4 ? "me@example.com" : null)
        {
            SupportsDeviceTrust = true,
            UserKeySecurityVersion = 2,
            UserKeyId = "00112233445566778899aabbccddeeff",
            AccountCryptographicState = CreateV2AccountCryptographicState(),
        };
    }

    private static PersistedAccountCryptographicState CreateV2AccountCryptographicState() => new(
        2,
        "7.wrapped-private-key",
        "public-key",
        "signed-public-key",
        "7.wrapped-signing-key",
        "verifying-key",
        "signed-security-state");
}
