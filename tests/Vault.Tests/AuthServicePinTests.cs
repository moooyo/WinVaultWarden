using System.Net;
using System.Security.Cryptography;
using Api;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class AuthServicePinTests
{
    private const string Email = "me@example.com";
    private const string Pin = "1234";
    private const int Iterations = 600_000;
    private const string RefreshedAccessToken =
        "e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln";

    private readonly CryptoService _crypto = new();

    [Fact]
    public async Task UnlockWithPinAsync_CorrectPin_RefreshesTokenAndResetsFailedAttempts()
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin("https://vault.example"), 1, Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
            PinFailedAttempts = 3,
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync(Pin, TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.Equal(fixture.UserKey.FullKey, session.UserKey!.FullKey);
        Assert.Equal(RefreshedAccessToken, session.AccessToken);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal("new-refresh", persisted.RefreshToken);
        Assert.Equal(0, persisted.PinFailedAttempts);
        Assert.Equal(fixture.PinProtectedUserKey, persisted.PinProtectedUserKey);
    }

    [Fact]
    public async Task UnlockWithPinAsync_WhenRemotePolicyAppearsWhileLockedClearsPinAndRelocks()
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin("https://vault.example"),
            1,
            Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
        });
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"access_token":"e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SyncJson(removePinUnlock: true)));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.PinCleared>(result);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Null(persisted.PinProtectedUserKey);
        Assert.Equal(0, persisted.PinFailedAttempts);
    }

    [Fact]
    public async Task SetAndUnlockPin_MixedEmailAndCustomMasterSalt_UsesNormalizedEmailDomain()
    {
        const string mixedEmail = " ME@EXAMPLE.COM ";
        const string customSalt = "Case-Sensitive-Master-Salt";
        const string pin = "123456";
        using var userKey = new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var masterKey = _crypto.DeriveMasterKey(
            "correct horse battery staple",
            customSalt,
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

        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            Email,
            mixedEmail,
            new ServerOrigin("https://vault.example"));
        session.TryCommit(lease, () =>
        {
            session.SetTokens("access", "stored-refresh");
            session.SetUnlockedKey(userKey);
        });
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            lease.Endpoints,
            lease.Generation,
            mixedEmail,
            "device-id",
            "stored-refresh",
            protectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            customSalt));
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var auth = CreateAuthService(handler, session, tokenStore);

        new PinService(_crypto, session, tokenStore).SetPin(pin);
        await auth.LockAsync(TestContext.Current.CancellationToken);
        var result = await auth.UnlockWithPinAsync(
            pin,
            TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.Success>(result);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(customSalt, persisted.MasterPasswordSalt);
        Assert.NotNull(persisted.PinProtectedUserKey);
    }

    [Fact]
    public async Task UnlockWithPinAsync_WrongPin_ReturnsFailureIncrementsAttemptsAndStaysLocked()
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin("https://vault.example"), 1, Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
            PinFailedAttempts = 0,
        });
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync("9999", TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Contains("4", failure.Message);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Empty(handler.Requests);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Equal(1, persisted.PinFailedAttempts);
        Assert.Equal(fixture.PinProtectedUserKey, persisted.PinProtectedUserKey);
    }

    [Fact]
    public async Task UnlockWithPinAsync_FifthConsecutiveWrongPin_ClearsPin()
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin("https://vault.example"), 1, Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
            PinFailedAttempts = 4,
        });
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync("9999", TestContext.Current.CancellationToken);

        Assert.IsType<AuthResult.PinCleared>(result);
        Assert.Empty(handler.Requests);
        Assert.True(tokenStore.TryLoad(out var persisted));
        Assert.Null(persisted.PinProtectedUserKey);
        Assert.Equal(0, persisted.PinFailedAttempts);
    }

    [Fact]
    public async Task UnlockWithPinAsync_NoPinSet_ReturnsFailure()
    {
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, Email, new ServerOrigin("https://vault.example"), 1, Email,
            "device-id",
            "stored-refresh",
            "2.unrelated",
            KdfType.Pbkdf2,
            Iterations,
            null,
            null));
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore);

        var result = await service.UnlockWithPinAsync(Pin, TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal("未设置 PIN。", failure.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnlockWithPinAsync_PendingAccountDeletionBlocksBeforeKeyUnwrap()
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin("https://vault.example"),
            1,
            Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
        };
        tokenStore.Save(persisted);
        var tombstoneStore = new SequencedAccountDeletionTombstoneStore(
            PendingFor(persisted),
            blockAfterReadCount: 1);
        var handler = new FakeHttpMessageHandler();
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore, tombstoneStore);

        var result = await service.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.AccountDeletionRecoveryRequired, failure.Kind);
        Assert.Empty(handler.Requests);
        Assert.Null(session.UserKey);
    }

    [Fact]
    public async Task UnlockWithPinAsync_TombstoneAppearingBeforeSuccessRelocksSession()
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin("https://vault.example"),
            1,
            Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
        };
        tokenStore.Save(persisted);
        var tombstoneStore = new SequencedAccountDeletionTombstoneStore(
            PendingFor(persisted),
            blockAfterReadCount: 3);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"access_token":"e30.eyJzdWIiOiJtZUBleGFtcGxlLmNvbSJ9.c2ln","refresh_token":"new-refresh","expires_in":3600,"token_type":"Bearer","scope":"api offline_access"}"""));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, DevicesJson()));
        var session = new VaultSession();
        var service = CreateAuthService(handler, session, tokenStore, tombstoneStore);

        var result = await service.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken);

        var failure = Assert.IsType<AuthResult.Failure>(result);
        Assert.Equal(AuthFailureKind.AccountDeletionRecoveryRequired, failure.Kind);
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.Equal(3, tombstoneStore.ReadCount);
    }

    [Theory]
    [InlineData(AccountDeletionTombstoneReadStatus.Corrupt)]
    [InlineData(AccountDeletionTombstoneReadStatus.Unavailable)]
    public async Task UnlockWithPinAsync_UnreadableDeletionLedgerFailsClosed(
        AccountDeletionTombstoneReadStatus readStatus)
    {
        var fixture = CreatePinFixture();
        var tokenStore = new MemoryTokenStore();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin("https://vault.example"),
            1,
            Email,
            "device-id",
            "stored-refresh",
            fixture.ProtectedUserKey,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null)
        {
            PinProtectedUserKey = fixture.PinProtectedUserKey,
        };
        tokenStore.Save(persisted);
        var tombstoneStore = new SequencedAccountDeletionTombstoneStore(
            PendingFor(persisted),
            blockAfterReadCount: int.MaxValue,
            forcedStatus: readStatus);
        var handler = new FakeHttpMessageHandler();
        var service = CreateAuthService(
            handler,
            new VaultSession(),
            tokenStore,
            tombstoneStore);

        var result = await service.UnlockWithPinAsync(
            Pin,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AuthFailureKind.AccountDeletionRecoveryRequired,
            Assert.IsType<AuthResult.Failure>(result).Kind);
        Assert.Empty(handler.Requests);
    }

    private AuthService CreateAuthService(
        FakeHttpMessageHandler handler,
        VaultSession session,
        MemoryTokenStore tokenStore,
        IAccountDeletionTombstoneStore? accountDeletionTombstones = null)
    {
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin("https://vault.example"));
        var decryptor = new VaultDecryptor(_crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var provider = new FakeVaultApiClientProvider().AddAnonymous<IIdentityApiClient>(api).AddAuthenticated<IReadonlyApiClient>(api);
        var bootstrapper = new VaultBootstrapper(
            new SyncService(provider, decryptor, session, cache, tokenStore),
            provider,
            session);
        var refresher = new TokenRefreshService(provider, session, tokenStore);
        return new AuthService(
            provider,
            _crypto,
            session,
            tokenStore,
            bootstrapper,
            cache,
            refresher,
            accountDeletionTombstones: accountDeletionTombstones);
    }

    private PinFixture CreatePinFixture()
    {
        var userKey = new SymmetricCryptoKey(Enumerable.Range(64, 64).Select(i => (byte)i).ToArray());
        var masterKey = _crypto.DeriveMasterKey("correct horse battery staple", Email, KdfType.Pbkdf2, Iterations, null, null);
        var stretchedKey = _crypto.StretchMasterKey(masterKey);
        var protectedUserKey = _crypto.Encrypt(userKey.FullKey, stretchedKey).ToString();

        var pinMasterKey = _crypto.DeriveMasterKey(Pin, Email, KdfType.Pbkdf2, Iterations, null, null);
        var pinProtectedUserKey = _crypto.ProtectUserKey(pinMasterKey, userKey).ToString();

        return new PinFixture(userKey, protectedUserKey, pinProtectedUserKey);
    }

    private static string SyncJson(bool removePinUnlock = false) => removePinUnlock
        ? """
          {"object":"sync","profile":{"id":"me@example.com","email":"me@example.com","name":"Me","securityStamp":"stamp-1",
           "organizations":[{"id":"org-1","name":"Org","status":2,"type":2,"enabled":true}]},
           "folders":[],"ciphers":[],"policies":[{"id":"policy-1","organizationId":"org-1",
           "type":14,"enabled":true,"data":{}}]}
          """
        : """{"object":"sync","profile":{"id":"me@example.com","email":"me@example.com","name":"Me","securityStamp":"stamp-1"},"folders":[],"ciphers":[]}""";

    private static string DevicesJson() =>
        """{"data":[{"id":"d1","name":"Desktop","type":6,"identifier":"d1","creationDate":"2026-06-24T00:00:00Z","isTrusted":false,"object":"device"}],"object":"list","continuationToken":null}""";

    private static AccountDeletionTombstoneRecord PendingFor(PersistedSession persisted)
    {
        var now = DateTimeOffset.UtcNow;
        return new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            1,
            now,
            now,
            new AccountDeletionBinding(
                persisted.AccountId,
                persisted.Email,
                persisted.Endpoints!,
                "11111111-1111-1111-1111-111111111111"),
            AccountDeletionStage.Prepared);
    }

    private sealed class SequencedAccountDeletionTombstoneStore(
        AccountDeletionTombstoneRecord pending,
        int blockAfterReadCount,
        AccountDeletionTombstoneReadStatus? forcedStatus = null)
        : IAccountDeletionTombstoneStore
    {
        public int ReadCount { get; private set; }

        public AccountDeletionTombstoneReadResult Read(AccountDeletionBinding binding)
        {
            var all = ReadAll();
            if (all.Status != AccountDeletionTombstoneReadStatus.Found)
                return AccountDeletionTombstoneReadResult.Absent;
            return pending.Binding == binding
                ? AccountDeletionTombstoneReadResult.Found(pending)
                : AccountDeletionTombstoneReadResult.Absent;
        }

        public AccountDeletionTombstoneReadAllResult ReadAll()
        {
            ReadCount++;
            if (forcedStatus == AccountDeletionTombstoneReadStatus.Corrupt)
                return AccountDeletionTombstoneReadAllResult.Corrupt;
            if (forcedStatus == AccountDeletionTombstoneReadStatus.Unavailable)
                return AccountDeletionTombstoneReadAllResult.Unavailable;
            return ReadCount >= blockAfterReadCount
                ? AccountDeletionTombstoneReadAllResult.Found([pending])
                : AccountDeletionTombstoneReadAllResult.Absent;
        }

        public AccountDeletionTombstoneRecord? TryCreate(
            AccountDeletionBinding binding,
            DateTimeOffset createdAt) => throw new NotSupportedException();
        public AccountDeletionTombstoneRecord? TryAdvance(
            AccountDeletionTombstoneRecord expected,
            AccountDeletionStage nextStage,
            DateTimeOffset updatedAt) => throw new NotSupportedException();
        public bool Remove(
            AccountDeletionTombstoneRecord expected,
            AccountDeletionTombstoneRemovalReason reason) => throw new NotSupportedException();
    }

    private sealed record PinFixture(SymmetricCryptoKey UserKey, string ProtectedUserKey, string PinProtectedUserKey);
}
