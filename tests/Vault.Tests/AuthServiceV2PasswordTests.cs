using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AuthServiceV2PasswordTests
{
    private const string Server = "https://vault.example";
    private const string Email = "v2@example.com";
    private const string AccountId = "account-v2";
    private const string Password = "correct horse battery staple";
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private const int Iterations = 600_000;

    private readonly CryptoService _crypto = new();

    [Fact]
    public async Task LoginAsync_V2PasswordLoginPersistsAuthenticatedAccountState()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var token = ValidLoginToken(material, protectedUserKey);
        var store = new MemoryTokenStore();
        var fixture = CreateFixture(
            [new ConnectTokenResult.Success(token)],
            store,
            sync: Sync(material));

        try
        {
            var result = await fixture.Auth.LoginAsync(
                Server,
                Email,
                Password,
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.Success>(result);
            Assert.True(fixture.Session.UsesV2UserKey);
            Assert.Equal(2, fixture.Session.SecurityVersion);
            Assert.Null(fixture.Session.UserKey);
            Assert.Equal(material.WrappedPrivateKey, fixture.Session.EncryptedPrivateKey);
            Assert.Equal(VaultState.Unlocked, fixture.Session.State);
            Assert.True(store.TryLoad(out var persisted));
            Assert.Equal(2, persisted.UserKeySecurityVersion);
            Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
            Assert.Equal(protectedUserKey, persisted.ProtectedUserKey);
            Assert.Equal(Email, persisted.MasterPasswordSalt);
            AssertPersistedAccountState(material, persisted.AccountCryptographicState);
        }
        finally
        {
            await fixture.Auth.LockAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LoginAsync_V2BootstrapAccountMismatchLocksAuthenticatedSession()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var sync = Sync(material);
        var mismatchedSync = sync with
        {
            Profile = sync.Profile! with
            {
                Id = "another-account",
            },
        };
        var store = new MemoryTokenStore();
        var fixture = CreateFixture(
            [new ConnectTokenResult.Success(
                ValidLoginToken(material, protectedUserKey))],
            store,
            sync: mismatchedSync);

        var result = await fixture.Auth.LoginAsync(
            Server,
            Email,
            Password,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.False(fixture.Session.UsesV2UserKey);
        Assert.Null(fixture.Session.UserKey);
        Assert.Null(fixture.Session.AccessToken);
        Assert.Null(fixture.Session.RefreshToken);
        Assert.Equal(1, fixture.Network.SyncCalls);
        Assert.Equal(0, fixture.Network.DeviceCalls);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal(2, persisted.UserKeySecurityVersion);
        Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
    }

    [Fact]
    public async Task LoginAsync_KnownV2SessionRejectsLegacyDowngrade()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedV2UserKey = ProtectUserKey(material.UserKey);
        var store = new MemoryTokenStore();
        store.Save(PersistedV2Session(material, protectedV2UserKey));
        store.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "active-account",
            ServerEndpoints.FromWebVault(Server),
            8,
            "active@example.com",
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            "active-refresh",
            "2.active-protected-key",
            KdfType.Pbkdf2,
            Iterations,
            null,
            null));

        using var legacyUserKey = new SymmetricCryptoKey(
            RandomNumberGenerator.GetBytes(64));
        byte[]? masterKey = null;
        string protectedLegacyUserKey;
        try
        {
            masterKey = _crypto.DeriveMasterKey(
                Password,
                Email,
                KdfType.Pbkdf2,
                Iterations,
                null,
                null);
            using var stretchedKey = _crypto.StretchMasterKey(masterKey);
            protectedLegacyUserKey = _crypto.Encrypt(
                legacyUserKey.FullKey,
                stretchedKey).ToString();
        }
        finally
        {
            if (masterKey is not null)
                CryptographicOperations.ZeroMemory(masterKey);
        }

        var legacyToken = new TokenResponse(
            AccessToken(),
            "legacy-refresh",
            3600,
            "Bearer",
            "api offline_access",
            protectedLegacyUserKey,
            null,
            KdfType.Pbkdf2,
            Iterations,
            UserDecryptionOptions: new UserDecryptionOptionsResponse(
                true,
                new MasterPasswordUnlockResponse(
                    Kdf: PasswordKdf(),
                    MasterKeyEncryptedUserKey: null,
                    MasterKeyWrappedUserKey: protectedLegacyUserKey,
                    Salt: Email)));
        var fixture = CreateFixture(
            [new ConnectTokenResult.Success(legacyToken)],
            store);

        var result = await fixture.Auth.LoginAsync(
            Server,
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.InvalidResponse, failure.Kind);
        Assert.Equal(0, fixture.Network.TotalCalls);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.True(store.TryLoad(out var active));
        Assert.Equal("active-account", active.AccountId);
        Assert.True(store.TryLoad(
            AccountId,
            ServerEndpoints.FromWebVault(Server),
            out var persisted));
        Assert.Equal(2, persisted.UserKeySecurityVersion);
        Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
        Assert.Equal("refresh-1", persisted.RefreshToken);
    }

    [Fact]
    public async Task LoginAsync_AccessTokenEmailMismatchFailsClosed()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var token = ValidLoginToken(material, protectedUserKey) with
        {
            AccessToken = AccessToken("another@example.com"),
        };

        await AssertLoginFailsClosedAsync(material, token);
    }

    [Fact]
    public async Task LoginAsync_MissingTopLevelKeyUsesNestedMasterPasswordUnlock()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var token = ValidLoginToken(material, protectedUserKey) with
        {
            Key = null,
        };
        var store = new MemoryTokenStore();
        var fixture = CreateFixture(
            [new ConnectTokenResult.Success(token)],
            store,
            sync: Sync(material));

        try
        {
            var result = await fixture.Auth.LoginAsync(
                Server,
                Email,
                Password,
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.Success>(result);
            Assert.True(fixture.Session.UsesV2UserKey);
            Assert.True(store.TryLoad(out var persisted));
            Assert.Equal(protectedUserKey, persisted.ProtectedUserKey);
            Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
        }
        finally
        {
            await fixture.Auth.LockAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LoginLockUnlock_V2PasswordRoundTripRestoresV2Session()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var store = new MemoryTokenStore();
        var fixture = CreateFixture(
            [
                new ConnectTokenResult.Success(
                    ValidLoginToken(material, protectedUserKey)),
                new ConnectTokenResult.Success(RefreshToken()),
            ],
            store,
            sync: Sync(material));

        try
        {
            Assert.IsType<AuthResult.Success>(await fixture.Auth.LoginAsync(
                Server,
                Email,
                Password,
                TestContext.Current.CancellationToken));

            await fixture.Auth.LockAsync(TestContext.Current.CancellationToken);

            Assert.Equal(VaultState.Locked, fixture.Session.State);
            Assert.False(fixture.Session.UsesV2UserKey);
            Assert.Null(fixture.Session.UserKey);

            var unlocked = await fixture.Auth.UnlockAsync(
                Password,
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.Success>(unlocked);
            Assert.True(fixture.Session.UsesV2UserKey);
            Assert.Equal(2, fixture.Session.SecurityVersion);
            Assert.Null(fixture.Session.UserKey);
            Assert.Equal(material.WrappedPrivateKey, fixture.Session.EncryptedPrivateKey);
            Assert.Equal(VaultState.Unlocked, fixture.Session.State);
            Assert.Equal(2, fixture.ControlledIdentity.ConnectCallCount);
            Assert.Equal(2, fixture.Network.SyncCalls);
            Assert.True(store.TryLoad(out var persisted));
            Assert.Equal(2, persisted.UserKeySecurityVersion);
            Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
            AssertPersistedAccountState(material, persisted.AccountCryptographicState);
        }
        finally
        {
            await fixture.Auth.LockAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RestartedService_UnlocksPersistedV2PasswordSession()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var store = new MemoryTokenStore();
        var cacheStore = new FakeCacheStore();
        var first = CreateFixture(
            [new ConnectTokenResult.Success(
                ValidLoginToken(material, protectedUserKey))],
            store,
            cacheStore,
            Sync(material));
        TestFixture? restarted = null;
        try
        {
            Assert.IsType<AuthResult.Success>(await first.Auth.LoginAsync(
                Server,
                Email,
                Password,
                TestContext.Current.CancellationToken));
            await first.Auth.LockAsync(TestContext.Current.CancellationToken);

            restarted = CreateFixture(
                [new ConnectTokenResult.Success(RefreshToken())],
                store,
                cacheStore,
                Sync(material));
            var result = await restarted.Auth.UnlockAsync(
                Password,
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.Success>(result);
            Assert.True(restarted.Session.UsesV2UserKey);
            Assert.Equal(2, restarted.Session.SecurityVersion);
            Assert.Null(restarted.Session.UserKey);
            Assert.Equal(material.WrappedPrivateKey, restarted.Session.EncryptedPrivateKey);
            Assert.Equal(VaultState.Unlocked, restarted.Session.State);
            Assert.Equal(1, restarted.ControlledIdentity.ConnectCallCount);
            Assert.True(store.TryLoad(out var persisted));
            Assert.Equal(material.UserKey.KeyIdHex, persisted.UserKeyId);
            AssertPersistedAccountState(material, persisted.AccountCryptographicState);
        }
        finally
        {
            if (restarted is not null)
                await restarted.Auth.LockAsync(CancellationToken.None);
            await first.Auth.LockAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RestartedService_UnlocksTransitionalV2StateWithoutOptionalPublicFields()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var transitionalAccountKeys = TransitionalAccountKeys(material);
        var store = new MemoryTokenStore();
        var cacheStore = new FakeCacheStore();
        var first = CreateFixture(
            [new ConnectTokenResult.Success(
                ValidLoginToken(material, protectedUserKey) with
                {
                    AccountKeys = transitionalAccountKeys,
                })],
            store,
            cacheStore,
            Sync(material, transitionalAccountKeys));
        TestFixture? restarted = null;
        try
        {
            Assert.IsType<AuthResult.Success>(await first.Auth.LoginAsync(
                Server,
                Email,
                Password,
                TestContext.Current.CancellationToken));
            Assert.True(store.TryLoad(out var persistedAfterLogin));
            var state = Assert.IsType<PersistedAccountCryptographicState>(
                persistedAfterLogin.AccountCryptographicState);
            Assert.True(state.HasValidShape);
            Assert.Null(state.PublicKey);
            Assert.Null(state.SignedPublicKey);
            Assert.Null(state.VerifyingKey);

            await first.Auth.LockAsync(TestContext.Current.CancellationToken);

            restarted = CreateFixture(
                [new ConnectTokenResult.Success(RefreshToken())],
                store,
                cacheStore,
                Sync(material, transitionalAccountKeys));
            var result = await restarted.Auth.UnlockAsync(
                Password,
                TestContext.Current.CancellationToken);

            Assert.IsType<AuthResult.Success>(result);
            Assert.True(restarted.Session.UsesV2UserKey);
            Assert.Equal(2, restarted.Session.SecurityVersion);
            Assert.Equal(material.WrappedPrivateKey, restarted.Session.EncryptedPrivateKey);
            Assert.Equal(VaultState.Unlocked, restarted.Session.State);
            Assert.True(store.TryLoad(out var persistedAfterRestart));
            Assert.Equal(state, persistedAfterRestart.AccountCryptographicState);
        }
        finally
        {
            if (restarted is not null)
                await restarted.Auth.LockAsync(CancellationToken.None);
            await first.Auth.LockAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task UnlockAsync_AdoptsRefreshRotatedAfterPersistedSnapshotRead()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var initial = PersistedV2Session(material, protectedUserKey);
        var store = new RotateAfterFirstLoadTokenStore(
            initial,
            initial with
            {
                RefreshToken = "refresh-2",
            });
        ControlledIdentityApiClient? identity = null;
        identity = new ControlledIdentityApiClient(_ =>
        {
            var request = identity!.ConnectRequests[^1];
            ConnectTokenResult result = request.RefreshToken == "refresh-2"
                ? new ConnectTokenResult.Success(
                    RefreshToken() with
                    {
                        RefreshToken = "refresh-3",
                    })
                : new ConnectTokenResult.Error(
                    "stale refresh token",
                    400,
                    "invalid_grant");
            return Task.FromResult(result);
        });
        var fixture = CreateFixture(
            identity,
            store,
            sync: Sync(material));

        var result = await fixture.Auth.UnlockAsync(
            Password,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        var request = Assert.Single(identity.ConnectRequests);
        Assert.Equal("refresh-2", request.RefreshToken);
        Assert.True(store.TryLoad(out var persisted));
        Assert.Equal("refresh-3", persisted.RefreshToken);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        await fixture.Auth.LockAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnlockAsync_WrongV2PasswordReturnsInvalidCredentialsWithoutNetwork()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var store = new MemoryTokenStore();
        store.Save(PersistedV2Session(material, protectedUserKey));
        var identity = new NoNetworkIdentityApiClient();
        var fixture = CreateFixture(
            identity,
            store,
            sync: Sync(material));

        try
        {
            var result = await fixture.Auth.UnlockAsync(
                "wrong password",
                TestContext.Current.CancellationToken);

            var failure = Assert.IsType<AuthResult.Failure>(result);
            Assert.Equal(AuthFailureKind.InvalidCredentials, failure.Kind);
            Assert.Equal(0, identity.CallCount);
            Assert.Equal(0, fixture.Network.TotalCalls);
            Assert.Equal(VaultState.Locked, fixture.Session.State);
            Assert.False(fixture.Session.UsesV2UserKey);
            Assert.Null(fixture.Session.UserKey);
            Assert.Null(fixture.Session.AccessToken);
            Assert.Null(fixture.Session.RefreshToken);
        }
        finally
        {
            await fixture.Auth.LockAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LoginAsync_MismatchedContainedKeyIdFailsClosed()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var token = ValidLoginToken(material, protectedUserKey);
        var options = token.UserDecryptionOptions!;
        var unlock = options.MasterPasswordUnlock!;
        token = token with
        {
            UserDecryptionOptions = options with
            {
                MasterPasswordUnlock = unlock with
                {
                    ContainedKeyId = DifferentKeyId(material.UserKey.KeyIdHex),
                },
            },
        };

        await AssertLoginFailsClosedAsync(material, token);
    }

    [Fact]
    public async Task LoginAsync_TamperedV2AccountKeysFailClosedWithoutPersistence()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var publicKey = Convert.FromBase64String(material.AccountPublicKey);
        string tamperedPublicKey;
        try
        {
            publicKey[0] ^= 1;
            tamperedPublicKey = Convert.ToBase64String(publicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }

        var accountKeys = ToAccountKeysResponse(material);
        var token = ValidLoginToken(material, protectedUserKey) with
        {
            AccountKeys = accountKeys with
            {
                PublicKeyEncryptionKeyPair = accountKeys.PublicKeyEncryptionKeyPair! with
                {
                    PublicKey = tamperedPublicKey,
                },
            },
        };

        await AssertLoginFailsClosedAsync(material, token);
    }

    [Fact]
    public async Task LoginAsync_V2ForcedPasswordChangeRemovesPriorOfflineSession()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var store = new MemoryTokenStore();
        var priorSession = PersistedV2Session(material, protectedUserKey);
        store.Save(priorSession);
        var cacheStore = new FakeCacheStore();
        var token = ValidLoginToken(material, protectedUserKey) with
        {
            ForcePasswordReset = true,
        };
        var fixture = CreateFixture(
            [new ConnectTokenResult.Success(token)],
            store,
            cacheStore,
            Sync(material));
        fixture.Session.RestorePersistedSession(priorSession);
        var transport = material.UserKey.ExportTransport();
        try
        {
            fixture.Session.SetUnlockedKey(V2UserKey.ImportTransport(transport));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);

        var result = await fixture.Auth.LoginAsync(
            Server,
            Email,
            Password,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.SafeMessage, failure.Kind);
        Assert.Contains("cannot complete yet", failure.Message, StringComparison.Ordinal);
        Assert.False(store.TryLoad(out _));
        Assert.Equal(1, cacheStore.ClearCount);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.Equal(0, fixture.Network.TotalCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginAsync_PartialNestedKdfAndSaltFailClosed(bool omitKdf)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var token = ValidLoginToken(material, protectedUserKey);
        var options = token.UserDecryptionOptions!;
        var unlock = options.MasterPasswordUnlock!;
        token = token with
        {
            UserDecryptionOptions = options with
            {
                MasterPasswordUnlock = unlock with
                {
                    Kdf = omitKdf ? null : PasswordKdf(),
                    Salt = omitKdf ? Email : null,
                },
            },
        };

        await AssertLoginFailsClosedAsync(material, token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoginAsync_ConflictingUserKeyWrapperAliasesFailClosed(
        bool conflictWithTopLevelKey)
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var protectedUserKey = ProtectUserKey(material.UserKey);
        var token = ValidLoginToken(material, protectedUserKey);
        if (conflictWithTopLevelKey)
        {
            token = token with { Key = "2.AA==|AA==|AA==" };
        }
        else
        {
            var options = token.UserDecryptionOptions!;
            var unlock = options.MasterPasswordUnlock!;
            token = token with
            {
                UserDecryptionOptions = options with
                {
                    MasterPasswordUnlock = unlock with
                    {
                        MasterKeyEncryptedUserKey = "2.AA==|AA==|AA==",
                    },
                },
            };
        }

        await AssertLoginFailsClosedAsync(material, token);
    }

    private async Task AssertLoginFailsClosedAsync(
        V2AccountKeyMaterial material,
        TokenResponse token)
    {
        var store = new MemoryTokenStore();
        var fixture = CreateFixture(
            [new ConnectTokenResult.Success(token)],
            store,
            sync: Sync(material));

        try
        {
            var result = await fixture.Auth.LoginAsync(
                Server,
                Email,
                Password,
                TestContext.Current.CancellationToken);

            var failure = Assert.IsType<AuthResult.Failure>(result);
            Assert.Equal(AuthFailureKind.InvalidResponse, failure.Kind);
            Assert.False(store.TryLoad(out _));
            Assert.Equal(0, fixture.Network.TotalCalls);
            Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
            Assert.False(fixture.Session.UsesV2UserKey);
            Assert.Null(fixture.Session.UserKey);
            Assert.Null(fixture.Session.AccessToken);
            Assert.Null(fixture.Session.RefreshToken);
        }
        finally
        {
            await fixture.Auth.LockAsync(CancellationToken.None);
        }
    }

    private string ProtectUserKey(V2UserKey userKey)
    {
        byte[]? masterKey = null;
        byte[]? transport = null;
        try
        {
            masterKey = _crypto.DeriveMasterKey(
                Password,
                Email,
                KdfType.Pbkdf2,
                Iterations,
                null,
                null);
            using var stretchedKey = _crypto.StretchMasterKey(masterKey);
            transport = userKey.ExportTransport();
            return _crypto.Encrypt(transport, stretchedKey).ToString();
        }
        finally
        {
            if (masterKey is not null)
                CryptographicOperations.ZeroMemory(masterKey);
            if (transport is not null)
                CryptographicOperations.ZeroMemory(transport);
        }
    }

    private static TokenResponse ValidLoginToken(
        V2AccountKeyMaterial material,
        string protectedUserKey) => new(
            AccessToken(),
            "refresh-1",
            3600,
            "Bearer",
            "api offline_access",
            protectedUserKey,
            null,
            KdfType.Pbkdf2,
            Iterations,
            UserDecryptionOptions: new UserDecryptionOptionsResponse(
                true,
                new MasterPasswordUnlockResponse(
                    Kdf: PasswordKdf(),
                    MasterKeyEncryptedUserKey: null,
                    MasterKeyWrappedUserKey: protectedUserKey,
                    Salt: Email,
                    ContainedKeyId: material.UserKey.KeyIdHex)),
            AccountKeys: ToAccountKeysResponse(material));

    private static TokenResponse RefreshToken() => new(
        AccessToken(),
        "refresh-2",
        3600,
        "Bearer",
        "api offline_access",
        null,
        null,
        KdfType.Pbkdf2,
        Iterations,
        UserDecryptionOptions: new UserDecryptionOptionsResponse(true));

    private static PreloginKdfSettingsResponse PasswordKdf() => new(
        KdfType.Pbkdf2,
        Iterations,
        null,
        null);

    private static AccountKeysResponse ToAccountKeysResponse(
        V2AccountKeyMaterial material) => new(
            new PublicKeyEncryptionKeyPairResponse(
                material.WrappedPrivateKey,
                material.AccountPublicKey,
                material.SignedPublicKey),
            new SignatureKeyPairResponse(
                material.WrappedSigningKey,
                material.VerifyingKey),
            new AccountSecurityState(
                material.SecurityState,
                material.SecurityVersion));

    private static AccountKeysResponse TransitionalAccountKeys(
        V2AccountKeyMaterial material) => new(
            new PublicKeyEncryptionKeyPairResponse(
                material.WrappedPrivateKey,
                null,
                null),
            new SignatureKeyPairResponse(
                material.WrappedSigningKey,
                null),
            new AccountSecurityState(
                material.SecurityState,
                material.SecurityVersion));

    private static SyncResponse Sync(
        V2AccountKeyMaterial material,
        AccountKeysResponse? accountKeysOverride = null)
    {
        var accountKeys = JsonSerializer.SerializeToElement(
            accountKeysOverride ?? ToAccountKeysResponse(material),
            ApiJsonContext.Default.AccountKeysResponse);
        return new SyncResponse(
            "sync",
            new ProfileDto(AccountId, Email, "V2 User", null, null)
            {
                AccountKeys = accountKeys,
                SecurityStamp = "stamp-v2",
            },
            [],
            []);
    }

    private static PersistedSession PersistedV2Session(
        V2AccountKeyMaterial material,
        string protectedUserKey) => new(
            PersistedSession.CurrentSchemaVersion,
            AccountId,
            ServerEndpoints.FromWebVault(Server),
            7,
            Email,
            DeviceId,
            "refresh-1",
            protectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            Email)
        {
            HasMasterPassword = true,
            UserKeySecurityVersion = 2,
            UserKeyId = material.UserKey.KeyIdHex,
            AccountCryptographicState = new PersistedAccountCryptographicState(
                2,
                material.WrappedPrivateKey,
                material.AccountPublicKey,
                material.SignedPublicKey,
                material.WrappedSigningKey,
                material.VerifyingKey,
                material.SecurityState),
        };

    private static void AssertPersistedAccountState(
        V2AccountKeyMaterial material,
        PersistedAccountCryptographicState? state)
    {
        var persisted = Assert.IsType<PersistedAccountCryptographicState>(state);
        Assert.Equal(2, persisted.SecurityVersion);
        Assert.Equal(material.WrappedPrivateKey, persisted.WrappedPrivateKey);
        Assert.Equal(material.AccountPublicKey, persisted.PublicKey);
        Assert.Equal(material.SignedPublicKey, persisted.SignedPublicKey);
        Assert.Equal(material.WrappedSigningKey, persisted.WrappedSigningKey);
        Assert.Equal(material.VerifyingKey, persisted.VerifyingKey);
        Assert.Equal(material.SecurityState, persisted.SignedSecurityState);
    }

    private static string DifferentKeyId(string keyId) =>
        $"{(keyId[0] == '0' ? '1' : '0')}{keyId[1..]}";

    private static string AccessToken(string email = Email)
    {
        static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');

        var header = Encode("""{"alg":"none"}""");
        var payload = Encode($$"""{"sub":"{{AccountId}}","email":"{{email}}"}""");
        return $"{header}.{payload}.signature";
    }

    private static TestFixture CreateFixture(
        IEnumerable<ConnectTokenResult> connectResults,
        ITokenStore? tokenStore = null,
        FakeCacheStore? cacheStore = null,
        SyncResponse? sync = null)
    {
        var queue = new Queue<ConnectTokenResult>(connectResults);
        var identity = new ControlledIdentityApiClient(_ =>
        {
            if (queue.Count == 0)
            {
                return Task.FromException<ConnectTokenResult>(
                    new InvalidOperationException("Unexpected identity request."));
            }

            return Task.FromResult(queue.Dequeue());
        })
        {
            PreloginResult = new PreloginResponse(
                KdfType.Pbkdf2,
                Iterations,
                null,
                null),
        };
        return CreateFixture(identity, tokenStore, cacheStore, sync);
    }

    private static TestFixture CreateFixture(
        IIdentityApiClient identity,
        ITokenStore? tokenStore = null,
        FakeCacheStore? cacheStore = null,
        SyncResponse? sync = null)
    {
        var effectiveTokenStore = tokenStore ?? new MemoryTokenStore();
        var effectiveCacheStore = cacheStore ?? new FakeCacheStore();
        var session = new VaultSession();
        var network = new NetworkProbe();
        var read = new ControlledReadonlyApiClient(_ =>
        {
            network.SyncCalls++;
            return sync is null
                ? Task.FromException<SyncResponse>(
                    new InvalidOperationException("Unexpected sync request."))
                : Task.FromResult(sync);
        })
        {
            GetConfig = _ =>
            {
                network.ConfigCalls++;
                return Task.FromResult(new ConfigResponse(null, null, null));
            },
            GetDevices = _ =>
            {
                network.DeviceCalls++;
                return Task.FromResult(new ListResponse<DeviceDto>([], null, "list"));
            },
        };
        var provider = new FakeVaultApiClientProvider()
            .AddAnonymous<IIdentityApiClient>(identity)
            .AddAuthenticated<IReadonlyApiClient>(read)
            .AddAuthenticated<IServerConfigApiClient>(read);
        var crypto = new CryptoService();
        var decryptor = new VaultDecryptor(crypto);
        var cache = new VaultCacheService(effectiveCacheStore, decryptor, session);
        var syncService = new SyncService(
            provider,
            decryptor,
            session,
            cache,
            effectiveTokenStore);
        var bootstrapper = new VaultBootstrapper(syncService, provider, session);
        var refresher = new TokenRefreshService(
            provider,
            session,
            effectiveTokenStore);
        var auth = new AuthService(
            provider,
            crypto,
            session,
            effectiveTokenStore,
            bootstrapper,
            cache,
            refresher,
            deviceIdentifierProvider: new FixedDeviceIdentifierProvider(DeviceId));
        return new TestFixture(
            auth,
            session,
            identity as ControlledIdentityApiClient,
            network);
    }

    private sealed record TestFixture(
        AuthService Auth,
        VaultSession Session,
        ControlledIdentityApiClient? Identity,
        NetworkProbe Network)
    {
        public ControlledIdentityApiClient ControlledIdentity =>
            Identity ?? throw new InvalidOperationException(
                "The fixture does not use a controlled identity client.");
    }

    private sealed class NetworkProbe
    {
        public int SyncCalls { get; set; }
        public int ConfigCalls { get; set; }
        public int DeviceCalls { get; set; }
        public int TotalCalls => SyncCalls + ConfigCalls + DeviceCalls;
    }

    private sealed class RotateAfterFirstLoadTokenStore(
        PersistedSession initial,
        PersistedSession rotated) : ITokenStore
    {
        private readonly object _gate = new();
        private PersistedSession? _current = initial;
        private int _readCount;

        public bool TryLoad(out PersistedSession session)
        {
            lock (_gate)
            {
                if (_current is null)
                {
                    session = null!;
                    return false;
                }

                session = _current;
                if (++_readCount == 1)
                    _current = rotated;
                return true;
            }
        }

        public void Save(PersistedSession session)
        {
            if (!session.HasValidBinding)
                throw new InvalidDataException("The test session is invalid.");
            lock (_gate)
                _current = session;
        }

        public void Clear()
        {
            lock (_gate)
                _current = null;
        }
    }

    private sealed class NoNetworkIdentityApiClient : IIdentityApiClient
    {
        public int CallCount { get; private set; }

        public Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default) =>
            Unexpected<ConfigResponse>();

        public Task<PreloginResponse> PreloginAsync(
            string email,
            CancellationToken ct = default) => Unexpected<PreloginResponse>();

        public Task<ConnectTokenResult> ConnectTokenAsync(
            ConnectTokenRequest request,
            CancellationToken ct = default) => Unexpected<ConnectTokenResult>();

        public Task SendTwoFactorEmailLoginAsync(
            SendEmailLoginRequest request,
            CancellationToken ct = default) => Unexpected();

        private Task<T> Unexpected<T>()
        {
            CallCount++;
            return Task.FromException<T>(
                new InvalidOperationException("No network request was expected."));
        }

        private Task Unexpected()
        {
            CallCount++;
            return Task.FromException(
                new InvalidOperationException("No network request was expected."));
        }
    }

    private sealed class FixedDeviceIdentifierProvider(string deviceIdentifier)
        : IDeviceIdentifierProvider
    {
        public string GetDeviceIdentifier() => deviceIdentifier;
    }
}
