using System.Security.Cryptography;
using System.Text;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class PinRestartPolicyTests
{
    private const string Server = "https://vault.example";
    private const string Email = "me@example.com";
    private const string Password = "correct horse battery staple";
    private const string Pin = "123456";
    private const int Iterations = 600_000;

    [Theory]
    [InlineData(Pin)]
    [InlineData("999999")]
    public async Task RestartedSessionRequiresMasterPasswordBeforePinWithoutCountingAnAttempt(
        string enteredPin)
    {
        using var original = new Fixture(requireMasterPasswordOnRestart: true);
        original.RecordMasterPasswordProof();
        using var restarted = original.Restart();
        Assert.True(restarted.Pin.RequiresMasterPasswordBeforePin);

        var result = await restarted.Auth.UnlockWithPinAsync(
            enteredPin,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.True(restarted.Store.TryLoad(out var persisted));
        Assert.Equal(0, persisted.PinFailedAttempts);
        Assert.NotNull(persisted.PinProtectedUserKey);
        Assert.Equal(0, restarted.Identity.ConnectCallCount);
        Assert.Null(restarted.Session.UserKey);
    }

    [Fact]
    public async Task ActualMasterPasswordUnlockAllowsPinAfterLockButDoesNotAuthorizeARestart()
    {
        using var original = new Fixture(requireMasterPasswordOnRestart: true);
        using var restarted = original.Restart();

        Assert.IsType<AuthResult.Success>(await restarted.Auth.UnlockAsync(
            Password,
            TestContext.Current.CancellationToken));
        Assert.False(restarted.Pin.RequiresMasterPasswordBeforePin);
        await restarted.Auth.LockAsync(TestContext.Current.CancellationToken);
        Assert.IsType<AuthResult.Success>(await restarted.Auth.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken));
        Assert.Equal(2, restarted.Identity.ConnectCallCount);

        using var nextRestart = restarted.Restart();
        Assert.True(nextRestart.Pin.RequiresMasterPasswordBeforePin);
        Assert.IsType<AuthResult.Failure>(await nextRestart.Auth.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, nextRestart.Identity.ConnectCallCount);
    }

    [Fact]
    public async Task HelloUnlockDoesNotSubstituteForTheRequiredMasterPasswordVerification()
    {
        using var original = new Fixture(requireMasterPasswordOnRestart: true);
        using var restarted = original.Restart();

        Assert.IsType<AuthResult.Success>(await restarted.Auth.UnlockWithHelloAsync(
            Signature(),
            TestContext.Current.CancellationToken));
        Assert.True(restarted.Pin.RequiresMasterPasswordBeforePin);
        await restarted.Auth.LockAsync(TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(await restarted.Auth.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, restarted.Identity.ConnectCallCount);
        Assert.True(restarted.Store.TryLoad(out var persisted));
        Assert.Equal(0, persisted.PinFailedAttempts);
    }

    [Fact]
    public async Task DisabledRestartPolicyRetainsLegacyPinUnlockBehavior()
    {
        using var original = new Fixture(requireMasterPasswordOnRestart: false);
        using var restarted = original.Restart();

        Assert.False(restarted.Pin.RequireMasterPasswordOnRestart);
        Assert.False(restarted.Pin.RequiresMasterPasswordBeforePin);
        Assert.IsType<AuthResult.Success>(await restarted.Auth.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, restarted.Identity.ConnectCallCount);
    }

    [Fact]
    public void RestartPolicyCanBeChangedWithoutReplacingThePinProtector()
    {
        using var fixture = new Fixture(requireMasterPasswordOnRestart: false);
        fixture.RecordMasterPasswordProof();
        Assert.True(fixture.Store.TryLoad(out var before));

        fixture.Pin.SetRequireMasterPasswordOnRestart(true);

        Assert.True(fixture.Pin.RequireMasterPasswordOnRestart);
        Assert.True(fixture.Store.TryLoad(out var enabled));
        Assert.True(enabled.RequireMasterPasswordOnRestart);
        Assert.Equal(before.PinProtectedUserKey, enabled.PinProtectedUserKey);

        fixture.Pin.SetRequireMasterPasswordOnRestart(false);

        Assert.False(fixture.Pin.RequireMasterPasswordOnRestart);
        Assert.True(fixture.Store.TryLoad(out var disabled));
        Assert.False(disabled.RequireMasterPasswordOnRestart);
        Assert.Equal(before.PinProtectedUserKey, disabled.PinProtectedUserKey);
    }

    [Fact]
    public void EnablingRestartPolicyWithoutMasterPasswordProofBlocksPinUntilVerified()
    {
        using var fixture = new Fixture(requireMasterPasswordOnRestart: false);
        Assert.False(fixture.Pin.RequiresMasterPasswordBeforePin);

        fixture.Pin.SetRequireMasterPasswordOnRestart(true);

        Assert.True(fixture.Pin.RequiresMasterPasswordBeforePin);
        fixture.RecordMasterPasswordProof();
        Assert.False(fixture.Pin.RequiresMasterPasswordBeforePin);
    }

    [Fact]
    public void ReenrollingPinAfterClearingItDoesNotRestoreMasterPasswordProof()
    {
        using var fixture = new Fixture(requireMasterPasswordOnRestart: true);
        fixture.RecordMasterPasswordProof();
        Assert.False(fixture.Pin.RequiresMasterPasswordBeforePin);

        fixture.Pin.ClearPin();
        fixture.Pin.SetPin(Pin, requireMasterPasswordOnRestart: true);

        Assert.True(fixture.Pin.RequiresMasterPasswordBeforePin);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("endpoints")]
    [InlineData("protected-user-key")]
    [InlineData("pin-protector")]
    public void MasterPasswordProofIsBoundToTheExactAccountAndCredential(string changedField)
    {
        using var fixture = new Fixture(requireMasterPasswordOnRestart: true);
        fixture.RecordMasterPasswordProof();
        Assert.False(fixture.Pin.RequiresMasterPasswordBeforePin);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        var changed = changedField switch
        {
            "account" => persisted with { AccountId = "another-account" },
            "endpoints" => ChangeEndpoints(persisted),
            "protected-user-key" => persisted with { ProtectedUserKey = "2.changed-master-key-wrapper" },
            "pin-protector" => persisted with { PinProtectedUserKey = "2.changed-pin-key-wrapper" },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };
        fixture.Store.Save(changed);

        Assert.True(fixture.Pin.RequiresMasterPasswordBeforePin);
    }

    [Fact]
    public void MasterPasswordProofIsBoundToTheV2UserKeyIdentifier()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var session = new VaultSession();
        var store = new MemoryTokenStore();
        var lease = session.BeginAccountSession(
            Email,
            Email,
            ServerEndpoints.FromWebVault(Server));
        var transport = material.UserKey.ExportTransport();
        try
        {
            session.SetUnlockedKey(V2UserKey.ImportTransport(transport));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }

        try
        {
            store.Save(new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                lease.AccountId,
                lease.Endpoints,
                lease.Generation,
                Email,
                "device-id",
                "refresh",
                "2.master-key-wrapper",
                KdfType.Pbkdf2,
                Iterations,
                null,
                null)
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
            });
            var pin = new PinService(new CryptoService(), session, store);
            pin.SetPin(Pin, requireMasterPasswordOnRestart: true);
            Assert.True(store.TryLoad(out var persisted));
            Assert.True(PinUnlockSessionPolicy.RecordMasterPasswordVerification(
                session,
                store,
                lease,
                persisted));
            Assert.False(pin.RequiresMasterPasswordBeforePin);
            var keyId = persisted.UserKeyId!;
            store.Save(persisted with
            {
                UserKeyId = $"{(keyId[0] == '0' ? '1' : '0')}{keyId[1..]}",
            });

            Assert.True(pin.RequiresMasterPasswordBeforePin);
        }
        finally
        {
            session.Logout();
        }
    }

    [Fact]
    public void ClearPinRemovesMasterPasswordProofEvenIfTheOldRecordReappears()
    {
        using var fixture = new Fixture(requireMasterPasswordOnRestart: true);
        fixture.RecordMasterPasswordProof();
        Assert.True(fixture.Store.TryLoad(out var protectedRecord));
        Assert.False(fixture.Pin.RequiresMasterPasswordBeforePin);

        fixture.Pin.ClearPin();
        fixture.Store.Save(protectedRecord);

        Assert.True(fixture.Pin.RequiresMasterPasswordBeforePin);
    }

    [Fact]
    public async Task FiveWrongPinsClearMasterPasswordProofTogetherWithTheProtector()
    {
        using var fixture = new Fixture(requireMasterPasswordOnRestart: true);
        fixture.RecordMasterPasswordProof();
        Assert.True(fixture.Store.TryLoad(out var protectedRecord));
        await fixture.Auth.LockAsync(TestContext.Current.CancellationToken);

        AuthResult? result = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            result = await fixture.Auth.UnlockWithPinAsync(
                "999999",
                TestContext.Current.CancellationToken);
        }

        Assert.IsType<AuthResult.PinCleared>(result);
        Assert.True(fixture.Store.TryLoad(out var cleared));
        Assert.Null(cleared.PinProtectedUserKey);
        Assert.Equal(0, cleared.PinFailedAttempts);
        fixture.Store.Save(protectedRecord with { Generation = cleared.Generation });
        Assert.True(fixture.Pin.RequiresMasterPasswordBeforePin);
        Assert.Equal(0, fixture.Identity.ConnectCallCount);
    }

    [Theory]
    [InlineData(Pin)]
    [InlineData("999999")]
    public async Task RestartPolicyEnabledDuringPinDerivationIsEnforcedAtCommit(string enteredPin)
    {
        var crypto = new StretchCallbackCrypto();
        using var fixture = new Fixture(requireMasterPasswordOnRestart: false, crypto: crypto);
        var existingKey = fixture.Session.UserKey;
        var existingAccessToken = fixture.Session.AccessToken;
        var existingRefreshToken = fixture.Session.RefreshToken;
        crypto.AfterStretch = () => fixture.Pin.SetRequireMasterPasswordOnRestart(true);

        var result = await fixture.Auth.UnlockWithPinAsync(
            enteredPin,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(1, crypto.CallbackCount);
        Assert.True(fixture.Store.TryLoad(out var persisted));
        Assert.True(persisted.RequireMasterPasswordOnRestart);
        Assert.Equal(0, persisted.PinFailedAttempts);
        Assert.NotNull(persisted.PinProtectedUserKey);
        Assert.Equal(0, fixture.Identity.ConnectCallCount);
        Assert.Same(existingKey, fixture.Session.UserKey);
        Assert.Equal(existingAccessToken, fixture.Session.AccessToken);
        Assert.Equal(existingRefreshToken, fixture.Session.RefreshToken);
    }

    [Fact]
    public async Task LogoutRemovesMasterPasswordProofEvenIfTheOldSavedRecordReappears()
    {
        using var fixture = new Fixture(requireMasterPasswordOnRestart: true);
        fixture.RecordMasterPasswordProof();
        Assert.False(fixture.Pin.RequiresMasterPasswordBeforePin);
        Assert.True(fixture.Store.TryLoad(out var oldRecord));

        await fixture.Auth.LogoutAsync(TestContext.Current.CancellationToken);
        fixture.Store.Save(oldRecord);

        Assert.True(fixture.Pin.RequiresMasterPasswordBeforePin);
        Assert.IsType<AuthResult.Failure>(await fixture.Auth.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Identity.ConnectCallCount);
        Assert.Null(fixture.Session.UserKey);
    }

    private static PersistedSession ChangeEndpoints(PersistedSession persisted)
    {
        var endpoints = ServerEndpoints.FromWebVault("https://other.example");
        return persisted with
        {
            Origin = endpoints.WebVault.Origin,
            Endpoints = endpoints,
        };
    }

    private static byte[] Signature() => Enumerable.Repeat((byte)5, 256).ToArray();

    private static string AccessToken()
    {
        static string Encode(string value) => Convert
            .ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var header = Encode("{}");
        var payload = Encode($$"""{"sub":"{{Email}}"}""");
        return $"{header}.{payload}.signature";
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CryptoService _crypto;

        public Fixture(bool requireMasterPasswordOnRestart, CryptoService? crypto = null)
            : this(new MemoryTokenStore(), crypto)
        {
            var lease = Session.BeginAccountSession(
                Email,
                Email,
                ServerEndpoints.FromWebVault(Server));
            using var userKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
            var masterKey = _crypto.DeriveMasterKey(
                Password,
                Email,
                KdfType.Pbkdf2,
                Iterations,
                null,
                null);
            string protectedUserKey;
            try
            {
                protectedUserKey = _crypto.ProtectUserKey(masterKey, userKey).ToString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }

            Session.SetUnlockedKey(new SymmetricCryptoKey(userKey.FullKey.ToArray()));
            Session.SetTokens("old-access", "old-refresh");
            Store.Save(new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                lease.AccountId,
                lease.Endpoints,
                lease.Generation,
                Email,
                "device-id",
                "old-refresh",
                protectedUserKey,
                KdfType.Pbkdf2,
                Iterations,
                null,
                null)
            {
                HasMasterPassword = true,
            });
            Pin.SetPin(PinRestartPolicyTests.Pin, requireMasterPasswordOnRestart);
            new HelloUnlockService(_crypto, Session, Store).SetHello(Signature(), new byte[32]);
        }

        private Fixture(MemoryTokenStore store, CryptoService? crypto = null)
        {
            _crypto = crypto ?? new CryptoService();
            Store = store;
            Pin = new PinService(_crypto, Session, Store);
            Identity = new ControlledIdentityApiClient(_ => Task.FromResult<ConnectTokenResult>(
                new ConnectTokenResult.Success(new TokenResponse(
                    AccessToken(),
                    "rotated-refresh",
                    3600,
                    "Bearer",
                    "api offline_access",
                    null,
                    null,
                    KdfType.Pbkdf2,
                    Iterations))));
            var read = new ControlledReadonlyApiClient(_ => Task.FromResult(new SyncResponse(
                "sync",
                new ProfileDto(Email, Email, "Test Account", null, null)
                {
                    SecurityStamp = "stamp-1",
                },
                [],
                [])))
            {
                GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
                GetDevices = _ => Task.FromResult(new ListResponse<DeviceDto>([], null, "list")),
            };
            var provider = new FakeVaultApiClientProvider()
                .AddAnonymous<IIdentityApiClient>(Identity)
                .AddAuthenticated<IReadonlyApiClient>(read)
                .AddAuthenticated<IServerConfigApiClient>(read);
            var decryptor = new VaultDecryptor(_crypto);
            var cache = new VaultCacheService(new FakeCacheStore(), decryptor, Session);
            var sync = new SyncService(provider, decryptor, Session, cache, Store);
            var bootstrapper = new VaultBootstrapper(sync, provider, Session);
            var refresher = new TokenRefreshService(provider, Session, Store);
            Auth = new AuthService(provider, _crypto, Session, Store, bootstrapper, cache, refresher);
        }

        public VaultSession Session { get; } = new();
        public MemoryTokenStore Store { get; }
        public PinService Pin { get; }
        public ControlledIdentityApiClient Identity { get; }
        public AuthService Auth { get; }

        public Fixture Restart() => new(Store);

        public void RecordMasterPasswordProof()
        {
            Assert.True(Store.TryLoad(out var persisted));
            Assert.True(PinUnlockSessionPolicy.RecordMasterPasswordVerification(
                Session,
                Store,
                Session.CaptureLease(),
                persisted));
        }

        public void Dispose() => Session.Logout();
    }

    private sealed class StretchCallbackCrypto : CryptoService
    {
        public Action? AfterStretch { get; set; }
        public int CallbackCount { get; private set; }

        public override SymmetricCryptoKey StretchMasterKey(byte[] masterKey)
        {
            var stretched = base.StretchMasterKey(masterKey);
            var callback = AfterStretch;
            AfterStretch = null;
            try
            {
                if (callback is not null)
                {
                    CallbackCount++;
                    callback();
                }
                return stretched;
            }
            catch
            {
                stretched.Dispose();
                throw;
            }
        }
    }
}
