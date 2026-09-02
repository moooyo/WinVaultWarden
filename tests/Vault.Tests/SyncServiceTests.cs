using System.Net;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SyncServiceTests
{
    private const string Server = "https://vault.example";

    public static TheoryData<string?> InvalidSecurityStamps => new()
    {
        null!,
        string.Empty,
        " ",
        "stamp\rvalue",
        new string('s', 129),
    };

    [Fact]
    public async Task SyncAsync_OnSuccess_WritesCacheAndMarksSnapshotFresh()
    {
        var crypto = new CryptoService();
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var session = new VaultSession();
        // profile.id 必须等于租约 AccountId,否则 SyncService 门禁拒绝(账户错配)。
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(userKey);
        var decryptor = new VaultDecryptor(crypto);
        var store = new FakeCacheStore();
        var cache = new VaultCacheService(store, decryptor, session);
        var sync = new SyncService(FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api), decryptor, session, cache);

        await sync.SyncAsync(lease, TestContext.Current.CancellationToken);

        Assert.False(session.DataStale);
        Assert.True(store.TryLoadVaultCache(out _));
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task SyncAsync_SecurityStampChangeLocksAndRequestsLogoutBeforeCommit()
    {
        var callCount = 0;
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            SyncResponseFor(
                Interlocked.Increment(ref callCount) == 1 ? "First" : "Second",
                callCount == 1 ? "stamp-1" : "stamp-2")))
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var store = new FakeCacheStore();
        var oldLeaseWasCurrentDuringPurge = true;
        var stateDuringPurge = VaultState.Unlocked;
        store.BeforeClear = () =>
        {
            oldLeaseWasCurrentDuringPurge = session.IsCurrent(lease);
            stateDuringPurge = session.State;
        };
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            "device-id",
            "refresh-token",
            "2.protected-user-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null));
        var termination = new RecordingTerminationSink();
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(store, decryptor, session),
            tokenStore,
            termination: termination);

        await service.SyncAsync(lease, TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SyncAsync(lease, CancellationToken.None));

        Assert.Equal("The account security stamp changed.", exception.Message);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.False(oldLeaseWasCurrentDuringPurge);
        Assert.Equal(VaultState.Locked, stateDuringPurge);
        Assert.Empty(session.Ciphers);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, store.ClearCount);
        Assert.False(tokenStore.TryActivate(
            lease.AccountId,
            lease.Endpoints,
            out _));
        var notice = Assert.Single(termination.Notices);
        Assert.Equal(SessionTerminationReason.InvalidSecurityStamp, notice.Reason);
        Assert.True(session.IsCurrent(notice.InvalidatedSession));
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task SyncAsync_ForcePasswordResetLocksAndRequestsLogoutBeforeDecryptOrCache()
    {
        var response = SyncResponseFor("Forced", "stamp-1") with
        {
            Profile = SyncResponseFor("Forced", "stamp-1").Profile! with
            {
                ForcePasswordReset = true,
            },
        };
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(response));
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var store = new FakeCacheStore();
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            "device-id",
            "refresh-token",
            "2.protected-user-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null));
        var termination = new RecordingTerminationSink();
        var service = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(store, decryptor, session),
            tokenStore,
            termination);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SyncAsync(lease, TestContext.Current.CancellationToken));

        Assert.Equal("The account requires a master-password change.", exception.Message);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(1, store.ClearCount);
        Assert.False(tokenStore.TryActivate(
            lease.AccountId,
            lease.Endpoints,
            out _));
        Assert.Empty(session.Ciphers);
        var notice = Assert.Single(termination.Notices);
        Assert.Equal(SessionTerminationReason.RequiredPasswordChange, notice.Reason);
        Assert.True(session.IsCurrent(notice.InvalidatedSession));
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task SyncAsync_ForcePasswordResetPersistenceFailureStillLocksAndSignals()
    {
        var response = SyncResponseFor("Forced", "stamp-1") with
        {
            Profile = SyncResponseFor("Forced", "stamp-1").Profile! with
            {
                ForcePasswordReset = true,
            },
        };
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(response));
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            "device-id",
            "refresh-token",
            "2.protected-user-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        var tokenStore = new ThrowingPinRevocationStore(persisted, removeFails: true);
        var decryptor = new VaultDecryptor(new CryptoService());
        var cacheStore = new FakeCacheStore();
        var termination = new RecordingTerminationSink();
        var service = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(cacheStore, decryptor, session),
            tokenStore,
            termination);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SyncAsync(lease, TestContext.Current.CancellationToken));

        Assert.Contains("saved session could not be removed", exception.Message);
        Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.True(tokenStore.RemoveCalled);
        Assert.True(tokenStore.TryLoad(out _));
        Assert.Equal(1, cacheStore.ClearCount);
        var notice = Assert.Single(termination.Notices);
        Assert.Equal(SessionTerminationReason.RequiredPasswordChange, notice.Reason);
        Assert.True(session.IsCurrent(notice.InvalidatedSession));
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task SyncAsync_ConcurrentStampMismatchRequestsTerminationOnce()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var apiCalls = 0;
        var api = new ControlledReadonlyApiClient(_ =>
        {
            Interlocked.Increment(ref apiCalls);
            requestStarted.TrySetResult();
            return response.Task;
        });
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        Assert.True(session.TryCommitSecurityStamp(lease, "stamp-1"));
        var decryptor = new VaultDecryptor(new CryptoService());
        var termination = new RecordingTerminationSink();
        var service = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session),
            termination: termination);

        var first = service.SyncAsync(lease, CancellationToken.None);
        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = service.SyncAsync(lease, CancellationToken.None);
        response.SetResult(SyncResponseFor("Changed", "stamp-2"));

        await Assert.ThrowsAsync<InvalidDataException>(() => first);
        var secondException = await Record.ExceptionAsync(() => second);

        Assert.True(
            secondException is OperationCanceledException
                or InvalidOperationException { Message: "Vault is locked." },
            $"Unexpected second sync outcome: {secondException}");
        Assert.Equal(1, apiCalls);
        var notice = Assert.Single(termination.Notices);
        Assert.Equal(SessionTerminationReason.InvalidSecurityStamp, notice.Reason);
        Assert.True(session.IsCurrent(notice.InvalidatedSession));
    }

    [Theory]
    [MemberData(nameof(InvalidSecurityStamps))]
    public async Task SyncAsync_InvalidSecurityStampRejectsWithoutCommitOrLogout(
        string? securityStamp)
    {
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            new SyncResponse(
                "sync",
                new ProfileDto("u1", "me@example.com", "Me", null, null)
                {
                    SecurityStamp = securityStamp,
                },
                [],
                [])));
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var store = new FakeCacheStore();
        var termination = new RecordingTerminationSink();
        var service = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(store, decryptor, session),
            termination: termination);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SyncAsync(lease, TestContext.Current.CancellationToken));

        Assert.Equal("Sync profile has an invalid security stamp.", exception.Message);
        Assert.Equal(VaultState.Unlocked, session.State);
        Assert.Equal(0, store.SaveCount);
        Assert.Empty(termination.Notices);
        Assert.Equal(
            SecurityStampComparison.NoBaseline,
            session.CompareSecurityStamp(lease, "stamp-1"));
    }

    [Fact]
    public async Task SyncAsync_StaleSecurityStampResponseCannotLogoutNewAccount()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ControlledReadonlyApiClient(_ =>
        {
            requestStarted.TrySetResult();
            return response.Task;
        });
        var session = new VaultSession();
        var stale = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            ServerEndpoints.ForSelfHosted("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        Assert.True(session.TryCommitSecurityStamp(stale, "stamp-a-1"));
        var decryptor = new VaultDecryptor(new CryptoService());
        var termination = new RecordingTerminationSink();
        var service = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session),
            termination: termination);

        var running = service.SyncAsync(stale, CancellationToken.None);
        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var current = session.BeginAccountSession(
            "account-b",
            "b@example.com",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(i => (byte)i).ToArray()));
        response.SetResult(new SyncResponse(
            "sync",
            new ProfileDto("account-a", "a@example.com", "A", null, null)
            {
                SecurityStamp = "stamp-a-2",
            },
            [],
            []));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.True(session.IsCurrent(current));
        Assert.Equal("account-b", session.Account.Id);
        Assert.Equal(VaultState.Unlocked, session.State);
        Assert.Empty(termination.Notices);
    }

    [Fact]
    public async Task SyncAsync_StaleResponseCannotPurgeNewSameAccountGeneration()
    {
        var requestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ControlledReadonlyApiClient(_ =>
        {
            requestStarted.TrySetResult();
            return response.Task;
        });
        var session = new VaultSession();
        var endpoints = ServerEndpoints.ForSelfHosted(Server);
        var stale = session.BeginAccountSession("u1", "me@example.com", endpoints);
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        Assert.True(session.TryCommitSecurityStamp(stale, "stamp-1"));
        var decryptor = new VaultDecryptor(new CryptoService());
        var cacheStore = new FakeCacheStore();
        var tokenStore = new MemoryTokenStore();
        var termination = new RecordingTerminationSink();
        var service = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(cacheStore, decryptor, session),
            tokenStore,
            termination);

        var running = service.SyncAsync(stale, CancellationToken.None);
        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var current = session.BeginAccountSession("u1", "me@example.com", endpoints);
        session.SetTokens("new-access", "new-refresh");
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(i => (byte)i).ToArray()));
        tokenStore.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            current.AccountId,
            current.Endpoints,
            current.Generation,
            "me@example.com",
            "new-device",
            "new-refresh",
            "2.new-protected-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null));
        cacheStore.SaveVaultCache(new VaultCacheEntry(
            VaultCacheEntry.CurrentSchemaVersion,
            current.AccountId,
            current.Endpoints,
            current.Generation,
            "me@example.com",
            DateTimeOffset.UtcNow,
            "{}"));
        response.SetResult(SyncResponseFor("Stale", "stamp-2"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.True(session.IsCurrent(current));
        Assert.Equal(VaultState.Unlocked, session.State);
        Assert.True(tokenStore.TryActivate("u1", endpoints, out var persisted));
        Assert.Equal(current.Generation, persisted.Generation);
        Assert.Equal("new-refresh", persisted.RefreshToken);
        Assert.Equal(0, cacheStore.ClearCount);
        Assert.Empty(termination.Notices);
    }

    [Fact]
    public async Task SyncAsync_CacheFailureDoesNotEstablishSecurityStampBaseline()
    {
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            SyncResponseFor("Me", "stamp-1")))
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var store = new FakeCacheStore
        {
            BeforeSave = () => throw new IOException("cache failed"),
        };
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(store, decryptor, session));

        await Assert.ThrowsAsync<IOException>(() =>
            service.SyncAsync(lease, TestContext.Current.CancellationToken));

        Assert.Equal(
            SecurityStampComparison.NoBaseline,
            session.CompareSecurityStamp(lease, "stamp-2"));
    }

    [Fact]
    public async Task SyncAndReconcileCipherCreate_RawWitnessConfirmsCipherSkippedByDecryptor()
    {
        const string rawWrappedKey = "not-an-enc-string";
        var response = new SyncResponse(
            "sync",
            new ProfileDto("u1", "me@example.com", "Me", null, null)
            {
                SecurityStamp = "stamp-1",
            },
            [],
            [
                new CipherDto(
                    "created-raw",
                    (int)CipherType.Login,
                    "invalid-name",
                    null,
                    rawWrappedKey,
                    null,
                    null,
                    false,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
            ]);
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(response));
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));
        var witness = new CipherCreateWitness(
            lease.AccountId,
            lease.Endpoints,
            CipherType.Login,
            organizationId: null,
            collectionIds: [],
            baselineCipherIds: [],
            CipherCreateWitness.ComputeKeyFingerprint(rawWrappedKey));

        var result = await service.SyncAndReconcileCipherCreateAsync(
            lease,
            new CipherCreatePending(witness, CommitConfirmed: false, ExpectedCipherId: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(CipherCreateReconciliationStatus.Confirmed, result.Status);
        Assert.Equal("created-raw", result.ConfirmedId);
        Assert.Empty(session.Ciphers);
        Assert.Equal(1, session.SkippedCipherCount);
    }

    [Fact]
    public async Task SyncAsync_AccountKeysOnly_CommitsCanonicalWrappedPrivateKey()
    {
        var crypto = new CryptoService();
        using var userKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(index => (byte)index).ToArray());
        var wrappedPrivateKey = crypto.Encrypt([1, 2, 3, 4], userKey).ToString();
        using var accountKeys = JsonDocument.Parse($$"""
        {
          "publicKeyEncryptionKeyPair": {
            "wrappedPrivateKey": {{JsonSerializer.Serialize(wrappedPrivateKey)}},
            "publicKey": "v1-public-key"
          },
          "signatureKeyPair": null,
          "securityState": null
        }
        """);
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            new SyncResponse(
                "sync",
                new ProfileDto("u1", "me@example.com", "Me", null, null)
                {
                    AccountKeys = accountKeys.RootElement.Clone(),
                    SecurityStamp = "stamp-1",
                },
                [],
                [])));
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(userKey.FullKey.ToArray()));
        var decryptor = new VaultDecryptor(crypto);
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        await service.SyncAsync(lease, TestContext.Current.CancellationToken);

        Assert.Equal(wrappedPrivateKey, session.EncryptedPrivateKey);
    }

    [Fact]
    public async Task SyncAsync_RemoveUnlockWithPinPolicyClearsOnlyCurrentAccountPin()
    {
        var crypto = new CryptoService();
        var userKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SyncJson(removePinUnlock: true)));
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(userKey);
        var tokenStore = new MemoryTokenStore();
        var other = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "u2",
            ServerEndpoints.FromWebVault("https://other.example"),
            3,
            "other@example.com",
            "other-device",
            "other-refresh",
            "2.other",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            PinProtectedUserKey = "2.other-pin",
        };
        var current = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            "current-device",
            "current-refresh",
            "2.current",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            PinProtectedUserKey = "2.current-pin",
            PinFailedAttempts = 2,
        };
        tokenStore.Save(other);
        tokenStore.Save(current);
        var decryptor = new VaultDecryptor(crypto);
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var sync = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            cache,
            tokenStore);

        await sync.SyncAsync(lease, TestContext.Current.CancellationToken);

        Assert.True(tokenStore.TryLoad(out var savedCurrent));
        Assert.Null(savedCurrent.PinProtectedUserKey);
        Assert.Equal(0, savedCurrent.PinFailedAttempts);
        Assert.True(tokenStore.TryActivate(
            other.AccountId,
            other.Endpoints!,
            out var savedOther));
        Assert.Equal("2.other-pin", savedOther.PinProtectedUserKey);
    }

    [Fact]
    public async Task SyncAsync_WhenPinRevocationSaveFailsIsolatesAccountAndCacheBeforeSignaling()
    {
        var crypto = new CryptoService();
        var userKeyBytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SyncJson(removePinUnlock: true)));
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(userKeyBytes));
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            "device-id",
            "refresh-token",
            "2.current",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            PinProtectedUserKey = "2.current-pin",
        };
        var tokenStore = new ThrowingPinRevocationStore(persisted);
        var decryptor = new VaultDecryptor(crypto);
        var cacheStore = new FakeCacheStore();
        var cache = new VaultCacheService(cacheStore, decryptor, session);
        var termination = new RecordingTerminationSink();
        var sync = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            cache,
            tokenStore,
            termination);

        await Assert.ThrowsAsync<IOException>(() =>
            sync.SyncAsync(lease, TestContext.Current.CancellationToken));

        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.Empty(session.Policies);
        Assert.All(userKeyBytes, value => Assert.Equal(0, value));
        Assert.False(cacheStore.TryLoadVaultCache(out _));
        Assert.True(tokenStore.RemoveCalled);
        Assert.False(tokenStore.TryLoad(out _));
        Assert.False(((ITokenStore)tokenStore).TryActivate(
            lease.AccountId,
            lease.Endpoints,
            out _));
        Assert.Equal(1, cacheStore.SaveCount);
        Assert.Equal(2, cacheStore.ClearCount);
        Assert.False(cacheStore.TryLoadVaultCache(out _));
        var notice = Assert.Single(termination.Notices);
        Assert.Equal(SessionTerminationReason.PinPolicyEnforcementFailed, notice.Reason);
        Assert.True(session.IsCurrent(notice.InvalidatedSession));
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task SyncAsync_WhenPolicyCacheSaveFailsAfterPinRevocationLocksAndSignals()
    {
        var crypto = new CryptoService();
        var userKeyBytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SyncJson(removePinUnlock: true)));
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(userKeyBytes));
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            "device-id",
            "refresh-token",
            "2.current",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            PinProtectedUserKey = "2.current-pin",
        };
        var tokenStore = new MemoryTokenStore();
        tokenStore.Save(persisted);
        var decryptor = new VaultDecryptor(crypto);
        var cacheStore = new FakeCacheStore
        {
            BeforeSave = () => throw new IOException("Cache persistence failed."),
        };
        var termination = new RecordingTerminationSink();
        var sync = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(cacheStore, decryptor, session),
            tokenStore,
            termination);

        await Assert.ThrowsAsync<IOException>(() =>
            sync.SyncAsync(lease, TestContext.Current.CancellationToken));

        Assert.Equal(VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.Empty(session.Policies);
        Assert.False(cacheStore.TryLoadVaultCache(out _));
        Assert.True(tokenStore.TryLoad(out var stillPersisted));
        Assert.Null(stillPersisted.PinProtectedUserKey);
        Assert.Equal(0, stillPersisted.PinFailedAttempts);
        Assert.Equal(2, cacheStore.ClearCount);
        var notice = Assert.Single(termination.Notices);
        Assert.Equal(SessionTerminationReason.PinPolicyEnforcementFailed, notice.Reason);
        Assert.True(session.IsCurrent(notice.InvalidatedSession));
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task SyncAsync_WhenPinAndCachePersistenceAndAccountRemovalFailStillClearsCacheAndSignals()
    {
        var crypto = new CryptoService();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            SyncJson(removePinUnlock: true)));
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            "device-id",
            "refresh-token",
            "2.current",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            PinProtectedUserKey = "2.current-pin",
        };
        var tokenStore = new ThrowingPinRevocationStore(persisted, removeFails: true);
        var decryptor = new VaultDecryptor(crypto);
        var cacheStore = new FakeCacheStore
        {
            BeforeSave = () => throw new IOException("Cache persistence failed."),
        };
        var termination = new RecordingTerminationSink();
        var sync = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(cacheStore, decryptor, session),
            tokenStore,
            termination);

        var thrown = await Assert.ThrowsAsync<AggregateException>(() =>
            sync.SyncAsync(lease, TestContext.Current.CancellationToken));

        Assert.Contains(thrown.InnerExceptions, exception =>
            exception is IOException { Message: "PIN revocation persistence failed." });
        Assert.Contains(thrown.InnerExceptions, exception =>
            exception is IOException { Message: "Cache persistence failed." });
        Assert.Contains(thrown.InnerExceptions, exception => exception is InvalidDataException);
        Assert.True(tokenStore.RemoveCalled);
        Assert.True(((ITokenStore)tokenStore).TryActivate(
            lease.AccountId,
            lease.Endpoints,
            out var stillPersisted));
        Assert.Equal("2.current-pin", stillPersisted.PinProtectedUserKey);
        Assert.Equal(2, cacheStore.ClearCount);
        Assert.False(cacheStore.TryLoadVaultCache(out _));
        Assert.Equal(VaultState.Locked, session.State);
        var notice = Assert.Single(termination.Notices);
        Assert.Equal(SessionTerminationReason.PinPolicyEnforcementFailed, notice.Reason);
        Assert.True(session.IsCurrent(notice.InvalidatedSession));
    }

    [Fact]
    public async Task SyncAsync_FreshConfigAndProfilePublishCapabilitiesWithSnapshot()
    {
        var crypto = new CryptoService();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """
            {"object":"sync","profile":{"id":"u1","email":"me@example.com","name":"Me",
             "securityStamp":"stamp-1","premium":true,"premiumFromOrganization":false},"folders":[],"ciphers":[]}
            """));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """
            {"version":"2026.6.0","featureStates":{"pm-32009-new-item-types":true,
             "pm-19148-innovation-archive":true}}
            """));
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var provider = new FakeVaultApiClientProvider()
            .AddAuthenticated<IReadonlyApiClient>(api)
            .AddAuthenticated<IServerConfigApiClient>(api);
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(crypto);
        var service = new SyncService(
            provider,
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        await service.SyncAsync(lease, TestContext.Current.CancellationToken);

        Assert.True(session.Capabilities.CanWriteNewCipherTypes);
        Assert.True(session.Capabilities.SupportsArchiveProtocol);
        Assert.True(session.Capabilities.HasArchiveEntitlement);
        Assert.True(session.Capabilities.CanArchive);
        Assert.Equal(["/api/sync", "/api/config"],
            handler.Requests.Select(request => request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task SyncAsync_ConfigFailureRevokesPreviousCapabilitiesWithoutFailingSync()
    {
        var configCalls = 0;
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            new SyncResponse(
                "sync",
                new ProfileDto("u1", "me@example.com", "Me", null, null)
                {
                    Premium = true,
                    SecurityStamp = "stamp-1",
                },
                [],
                [])))
        {
            GetConfig = _ =>
            {
                configCalls++;
                throw new HttpRequestException("config unavailable");
            },
        };
        var provider = ConfiguredProvider(api);
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(
            new DecryptedVault(session.Account, [], [], 0, 0),
            capabilities: new VaultCapabilities(true, true, true));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            provider,
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        await service.SyncAsync(lease, TestContext.Current.CancellationToken);

        Assert.Equal(1, configCalls);
        Assert.Equal(VaultCapabilities.None, session.Capabilities);
    }

    [Fact]
    public async Task SyncAsync_FetchesConfigAgainAndDoesNotRetainPreviousTrueFlag()
    {
        var responses = new Queue<ConfigResponse>(
        [
            ConfigWith((VaultCapabilityResolver.NewCipherTypesFeatureKey, "true")),
            new ConfigResponse(null, null, null),
        ]);
        var configCalls = 0;
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            new SyncResponse(
                "sync",
                StampedProfile("u1", "me@example.com", "Me"),
                [],
                [])))
        {
            GetConfig = _ =>
            {
                configCalls++;
                return Task.FromResult(responses.Dequeue());
            },
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        await service.SyncAsync(lease, TestContext.Current.CancellationToken);
        Assert.True(session.Capabilities.CanWriteNewCipherTypes);

        await service.SyncAsync(lease, TestContext.Current.CancellationToken);

        Assert.Equal(2, configCalls);
        Assert.False(session.Capabilities.CanWriteNewCipherTypes);
    }

    [Fact]
    public async Task SyncAsync_ConfigFromExpiredLeaseCannotPopulateReplacementSession()
    {
        var configStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var configResult = new TaskCompletionSource<ConfigResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            new SyncResponse(
                "sync",
                StampedProfile("account-a", "a@example.com", "A"),
                [],
                [])))
        {
            GetConfig = async _ =>
            {
                configStarted.TrySetResult();
                return await configResult.Task;
            },
        };
        var session = new VaultSession();
        var stale = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            ServerEndpoints.ForSelfHosted("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        var running = service.SyncAsync(stale, CancellationToken.None);
        await configStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        session.BeginAccountSession(
            "account-b",
            "b@example.com",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(i => (byte)i).ToArray()));
        configResult.SetResult(ConfigWith(
            (VaultCapabilityResolver.NewCipherTypesFeatureKey, "true"),
            (VaultCapabilityResolver.LegacyArchiveFeatureKey, "true")));

        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        Assert.Equal("account-b", session.Account.Id);
        Assert.Equal(VaultCapabilities.None, session.Capabilities);
    }

    [Fact]
    public async Task SyncAsync_ConcurrentCallsForSameLeaseCommitInAdmissionOrder()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var api = new ControlledReadonlyApiClient(_ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstStarted.TrySetResult();
                return firstResult.Task;
            }

            secondStarted.TrySetResult();
            return Task.FromResult(SyncResponseFor("New"));
        })
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        var first = service.SyncAsync(lease, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = service.SyncAsync(lease, CancellationToken.None);

        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref callCount));

        firstResult.SetResult(SyncResponseFor("Old"));
        await first.WaitAsync(TestContext.Current.CancellationToken);
        await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await second.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Equal("N", session.Account.Initial);
    }

    [Fact]
    public async Task SyncAsync_NewLeaseDoesNotWaitForBlockedExpiredLease()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var api = new ControlledReadonlyApiClient(_ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstStarted.TrySetResult();
                return firstResult.Task;
            }

            return Task.FromResult(new SyncResponse(
                "sync",
                StampedProfile("account-b", "b@example.com", "B"),
                [],
                []));
        })
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var stale = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            ServerEndpoints.ForSelfHosted("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        var blocked = service.SyncAsync(stale, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var current = session.BeginAccountSession(
            "account-b",
            "b@example.com",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(i => (byte)i).ToArray()));

        await service.SyncAsync(current, TestContext.Current.CancellationToken);

        Assert.Equal("account-b", session.Account.Id);
        Assert.Equal(2, callCount);

        firstResult.SetResult(new SyncResponse(
            "sync",
            StampedProfile("account-a", "a@example.com", "A"),
            [],
            []));
        await Assert.ThrowsAsync<OperationCanceledException>(() => blocked);
        Assert.Equal("account-b", session.Account.Id);
    }

    [Fact]
    public async Task SyncAsync_ExpiredLeaseCannotSplitCurrentLeaseGate()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var api = new ControlledReadonlyApiClient(_ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstStarted.TrySetResult();
                return firstResult.Task;
            }

            secondStarted.TrySetResult();
            return Task.FromResult(new SyncResponse(
                "sync",
                StampedProfile("account-b", "b@example.com", "B"),
                [],
                []));
        })
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var expired = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            ServerEndpoints.ForSelfHosted("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var current = session.BeginAccountSession(
            "account-b",
            "b@example.com",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(64, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        var first = service.SyncAsync(current, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SyncAsync(expired, CancellationToken.None));
        var second = service.SyncAsync(current, CancellationToken.None);

        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Equal(1, callCount);

        firstResult.SetResult(new SyncResponse(
            "sync",
            StampedProfile("account-b", "b@example.com", "B"),
            [],
            []));
        await first.WaitAsync(TestContext.Current.CancellationToken);
        await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await second.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Equal("account-b", session.Account.Id);
    }

    [Fact]
    public async Task SyncAsync_CancelledWaiterDoesNotEnterApiOrBlockNextWaiter()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var laterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var api = new ControlledReadonlyApiClient(_ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstStarted.TrySetResult();
                return firstResult.Task;
            }

            laterStarted.TrySetResult();
            return Task.FromResult(SyncResponseFor("New"));
        })
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        var first = service.SyncAsync(lease, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.SyncAsync(lease, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, callCount);

        var next = service.SyncAsync(lease, CancellationToken.None);
        Assert.False(laterStarted.Task.IsCompleted);
        firstResult.SetResult(SyncResponseFor("Old"));
        await first.WaitAsync(TestContext.Current.CancellationToken);
        await next.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Equal("N", session.Account.Initial);
    }

    [Fact]
    public async Task SyncAsync_FailedHolderReleasesSameLeaseWaiter()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstResult = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var api = new ControlledReadonlyApiClient(_ =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                firstStarted.TrySetResult();
                return firstResult.Task;
            }

            return Task.FromResult(SyncResponseFor("Recovered"));
        })
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            new ServerOrigin(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        var failed = service.SyncAsync(lease, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var recovered = service.SyncAsync(lease, CancellationToken.None);
        firstResult.SetException(new HttpRequestException("sync unavailable"));

        await Assert.ThrowsAsync<HttpRequestException>(() => failed);
        await recovered.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, callCount);
        Assert.Equal("R", session.Account.Initial);
    }

    [Fact]
    public async Task SyncIfNeededAsync_CurrentCacheStillRunsAuthoritativeFullSync()
    {
        var fullSyncCalls = 0;
        var cachedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var api = new ControlledReadonlyApiClient(_ =>
        {
            fullSyncCalls++;
            return Task.FromResult(SyncResponseFor("Refreshed"));
        })
        {
            GetAccountRevisionDate = _ => throw new InvalidOperationException(
                "Catch-up must not trust the incomplete revision watermark."),
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            ServerEndpoints.ForSelfHosted(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        Assert.True(session.TryMarkDataStale(lease));
        var decryptor = new VaultDecryptor(new CryptoService());
        var store = new FakeCacheStore();
        store.SaveVaultCache(new VaultCacheEntry(
            VaultCacheEntry.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            cachedAt,
            "{}"));
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(store, decryptor, session));

        var synchronized = await service.SyncIfNeededAsync(
            lease,
            TestContext.Current.CancellationToken);

        Assert.True(synchronized);
        Assert.False(session.DataStale);
        Assert.Equal(1, fullSyncCalls);
        Assert.Equal(2, store.SaveCount);
        Assert.Equal("R", session.Account.Initial);
    }

    [Fact]
    public async Task SyncIfNeededAsync_OlderAccountRevisionCannotSuppressOrganizationCatchUp()
    {
        var fullSyncCalls = 0;
        var cachedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var api = new ControlledReadonlyApiClient(_ =>
        {
            fullSyncCalls++;
            return Task.FromResult(SyncResponseFor("Refreshed"));
        })
        {
            GetAccountRevisionDate = _ => Task.FromResult(
                cachedAt.AddDays(-1).ToUnixTimeMilliseconds()),
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            ServerEndpoints.ForSelfHosted(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var store = new FakeCacheStore();
        store.SaveVaultCache(new VaultCacheEntry(
            VaultCacheEntry.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "me@example.com",
            cachedAt,
            "{}"));
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(store, decryptor, session));

        var synchronized = await service.SyncIfNeededAsync(
            lease,
            TestContext.Current.CancellationToken);

        Assert.True(synchronized);
        Assert.Equal(1, fullSyncCalls);
        Assert.Equal("R", session.Account.Initial);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task SyncIfNeededAsync_FullSyncResponseForExpiredLeaseCannotSyncNewAccount()
    {
        var syncStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var syncResult = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fullSyncCalls = 0;
        var api = new ControlledReadonlyApiClient(_ =>
        {
            fullSyncCalls++;
            syncStarted.TrySetResult();
            return syncResult.Task;
        })
        {
            GetConfig = _ => Task.FromResult(new ConfigResponse(null, null, null)),
        };
        var session = new VaultSession();
        var staleLease = session.BeginAccountSession(
            "u1",
            "me@example.com",
            ServerEndpoints.ForSelfHosted(Server));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            ConfiguredProvider(api),
            decryptor,
            session,
            new VaultCacheService(new FakeCacheStore(), decryptor, session));

        var running = service.SyncIfNeededAsync(staleLease, CancellationToken.None);
        await syncStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        session.BeginAccountSession(
            "account-b",
            "b@example.com",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        syncResult.SetResult(SyncResponseFor("Unexpected"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, fullSyncCalls);
        Assert.Equal("account-b", session.CaptureLease().AccountId);
    }

    private static FakeVaultApiClientProvider ConfiguredProvider(
        ControlledReadonlyApiClient api) =>
        new FakeVaultApiClientProvider()
            .AddAuthenticated<IReadonlyApiClient>(api)
            .AddAuthenticated<IServerConfigApiClient>(api);

    private static ConfigResponse ConfigWith(params (string Key, string Json)[] values) =>
        new(null, null, null)
        {
            FeatureStates = values.ToDictionary(
                item => item.Key,
                item => System.Text.Json.JsonDocument.Parse(item.Json).RootElement.Clone(),
                StringComparer.Ordinal),
        };

    private static SyncResponse SyncResponseFor(
        string profileName,
        string securityStamp = "stamp-1") =>
        new(
            "sync",
            new ProfileDto("u1", "me@example.com", profileName, null, null)
            {
                SecurityStamp = securityStamp,
            },
            [],
            []);

    private static ProfileDto StampedProfile(
        string accountId,
        string email,
        string name) =>
        new(accountId, email, name, null, null)
        {
            SecurityStamp = "stamp-1",
        };

    private static string SyncJson(bool removePinUnlock = false) => removePinUnlock
        ? """
          {"object":"sync","profile":{"id":"u1","email":"me@example.com","name":"Me","securityStamp":"stamp-1",
           "organizations":[{"id":"org-1","name":"Org","status":2,"type":2,"enabled":true}]},
           "folders":[],"ciphers":[],"policies":[{"id":"policy-1","organizationId":"org-1",
           "type":14,"enabled":true,"data":{}}]}
          """
        : """{"object":"sync","profile":{"id":"u1","email":"me@example.com","name":"Me","securityStamp":"stamp-1"},"folders":[],"ciphers":[]}""";

    private sealed class ThrowingPinRevocationStore(
        PersistedSession persisted,
        bool removeFails = false) : ITokenStore
    {
        private PersistedSession? _persisted = persisted;

        public bool RemoveCalled { get; private set; }

        public bool TryLoad(out PersistedSession session)
        {
            session = _persisted!;
            return _persisted is not null;
        }

        public void Save(PersistedSession session) =>
            throw new IOException("PIN revocation persistence failed.");

        public void Clear()
        {
            _persisted = null;
        }

        public bool Remove(string accountId, ServerEndpoints endpoints)
        {
            RemoveCalled = true;
            if (removeFails)
                return false;
            if (_persisted?.AccountId == accountId && _persisted.Endpoints == endpoints)
                _persisted = null;
            return true;
        }

    }

    private sealed class RecordingTerminationSink : ISessionTerminationSink
    {
        public List<SessionTerminationNotice> Notices { get; } = [];

        public void Request(SessionTerminationNotice notice) => Notices.Add(notice);
    }
}
