using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using App.Services;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class DpapiTrustedDeviceApprovalStoreTests
{
    private const string RequestId = "11111111-2222-3333-4444-555555555555";
    private const string OtherRequestId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ThirdRequestId = "22222222-3333-4444-5555-666666666666";
    private const string FourthRequestId = "33333333-4444-5555-6666-777777777777";
    private const string DeviceId = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const string OtherDeviceId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";

    [Fact]
    public void PendingModel_UsesProtocolLifetimesAndOwnsPrivateKey()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var privateKey = NewPrivateKey();
        try
        {
            using var admin = TrustedDeviceApprovalPending.CreateWithDefaultLifetime(
                RequestId,
                privateKey,
                "account-1",
                Endpoints(),
                DeviceId,
                createdAt,
                TrustedDeviceApprovalRequestType.AdminApproval);
            using var login = TrustedDeviceApprovalPending.CreateWithDefaultLifetime(
                OtherRequestId,
                privateKey,
                "account-1",
                Endpoints(),
                DeviceId,
                createdAt,
                TrustedDeviceApprovalRequestType.AuthenticateAndUnlock);

            Assert.Equal(TimeSpan.FromDays(7), admin.ExpiresAt - admin.CreatedAt);
            Assert.Equal(TimeSpan.FromMinutes(15), login.ExpiresAt - login.CreatedAt);
            Assert.False(admin.IsExpired(admin.ExpiresAt.AddTicks(-1)));
            Assert.True(admin.IsExpired(admin.ExpiresAt));

            CryptographicOperations.ZeroMemory(privateKey);
            var copied = admin.CopyPrivateKeyPkcs8();
            try
            {
                AssertValidRsa2048(copied);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(copied);
            }

            admin.Dispose();
            Assert.Throws<ObjectDisposedException>(() => admin.CopyPrivateKeyPkcs8());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    [Fact]
    public void PendingModel_RejectsMalformedBindingsKeysAndLifetimes()
    {
        var key2048 = NewPrivateKey();
        var key1024 = NewPrivateKey(1024);
        var trailing = key2048.Concat(new byte[] { 0 }).ToArray();
        var createdAt = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        try
        {
            Assert.Throws<ArgumentException>(() => Pending(
                key1024,
                requestId: RequestId));
            Assert.Throws<ArgumentException>(() => Pending(
                trailing,
                requestId: RequestId));
            Assert.Throws<ArgumentException>(() => Pending(
                key2048,
                requestId: "not-a-guid"));
            Assert.Throws<ArgumentException>(() => new TrustedDeviceApprovalPending(
                RequestId,
                key2048,
                "account-1",
                Endpoints(),
                "not-a-guid",
                createdAt,
                createdAt.AddHours(12),
                TrustedDeviceApprovalRequestType.AdminApproval));
            Assert.Throws<ArgumentException>(() => new TrustedDeviceApprovalPending(
                RequestId,
                key2048,
                "account-1",
                Endpoints(),
                DeviceId,
                createdAt,
                createdAt.AddDays(9),
                TrustedDeviceApprovalRequestType.AdminApproval));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trailing);
            CryptographicOperations.ZeroMemory(key1024);
            CryptographicOperations.ZeroMemory(key2048);
        }
    }

    [Fact]
    public void FakeProtection_RestoresAcrossInstancesAndIsolatesBindings()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var key = NewPrivateKey();
        var alternateEndpoints = WithAlternateApi(Endpoints());
        using var firstPending = Pending(key, RequestId);
        using var sameBinding = Pending(key, OtherRequestId);
        using var otherBinding = Pending(
            key,
            OtherRequestId,
            accountId: "account-2",
            endpoints: alternateEndpoints,
            deviceId: "bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        try
        {
            var firstStore = new DpapiTrustedDeviceApprovalStore(path, protection);
            using var created = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(firstStore, firstPending));
            Assert.Null(TryCreate(firstStore, sameBinding));
            using var other = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(firstStore, otherBinding));

            var reopened = new DpapiTrustedDeviceApprovalStore(path, protection);
            using var recovered = AssertFound(reopened.Read(firstPending.Binding));
            Assert.Equal(RequestId, recovered.Pending.RequestId);
            Assert.Equal(TrustedDeviceApprovalRequestType.AdminApproval, recovered.Pending.RequestType);
            AssertPrivateKeyEquals(key, recovered.Pending);

            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                reopened.Read(firstPending.Binding, OtherRequestId).Status);
            using var recoveredOther = AssertFound(reopened.Read(
                otherBinding.Binding,
                OtherRequestId));
            Assert.Equal("account-2", recoveredOther.Pending.AccountId);
            Assert.Equal(alternateEndpoints, recoveredOther.Pending.Endpoints);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Theory]
    [InlineData(TrustedDeviceApprovalRemovalReason.Completed)]
    [InlineData(TrustedDeviceApprovalRemovalReason.Cancelled)]
    [InlineData(TrustedDeviceApprovalRemovalReason.Expired)]
    public void Remove_IsExactCasAndClearsRecordPrivateKey(
        TrustedDeviceApprovalRemovalReason reason)
    {
        var path = NewPath();
        var key = NewPrivateKey();
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
            var record = Assert.IsType<TrustedDeviceApprovalStoreRecord>(TryCreate(store, pending));
            using var wrongPending = Pending(key, RequestId);
            using var wrong = new TrustedDeviceApprovalStoreRecord(
                Guid.NewGuid(),
                record.Revision,
                wrongPending.Clone());

            Assert.False(store.Remove(wrong, reason));
            AssertPrivateKeyEquals(key, wrong.Pending);
            Assert.True(store.Remove(record, reason));
            Assert.Throws<ObjectDisposedException>(() => record.Pending.CopyPrivateKeyPkcs8());
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                store.Read(pending.Binding, RequestId).Status);

            using var replacement = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, pending));
            Assert.NotEqual(record.EntryId, replacement.EntryId);
            Assert.False(store.Remove(record, reason));
            using var stillPresent = AssertFound(store.Read(pending.Binding, RequestId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void RemoveAccountScope_RemovesEveryDeviceAndPreservesOtherScopes()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var key = NewPrivateKey();
        var endpoints = Endpoints();
        var alternateEndpoints = WithAlternateApi(endpoints);
        using var first = Pending(key, RequestId, endpoints: endpoints);
        using var second = Pending(
            key,
            OtherRequestId,
            endpoints: endpoints,
            deviceId: OtherDeviceId);
        using var otherEndpoint = Pending(
            key,
            ThirdRequestId,
            endpoints: alternateEndpoints);
        using var otherAccount = Pending(
            key,
            FourthRequestId,
            accountId: "account-2",
            endpoints: endpoints);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path, protection);
            using var firstRecord = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, first));
            using var secondRecord = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, second));
            using var otherEndpointRecord = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, otherEndpoint));
            using var otherAccountRecord = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, otherAccount));

            Assert.Equal(
                AccountScopedRemovalStatus.Removed,
                store.RemoveAccountScope("account-1", endpoints));

            var reopened = new DpapiTrustedDeviceApprovalStore(path, protection);
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                reopened.Read(first.Binding).Status);
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                reopened.Read(second.Binding).Status);
            using var preservedEndpoint = AssertFound(reopened.Read(otherEndpoint.Binding));
            using var preservedAccount = AssertFound(reopened.Read(otherAccount.Binding));
            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                reopened.RemoveAccountScope("account-1", endpoints));
            Assert.All(protection.ProtectPlaintexts, AssertCleared);
            Assert.All(protection.ProtectEntropies, AssertCleared);
            Assert.All(protection.ProtectOutputs, AssertCleared);
            Assert.All(protection.UnprotectProtectedPayloads, AssertCleared);
            Assert.All(protection.UnprotectEntropies, AssertCleared);
            Assert.All(protection.UnprotectPlaintexts, AssertCleared);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void RemoveAccountScope_RequiresAllSixEndpointsToMatch()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        var endpoints = Endpoints();
        var pendingEntries = new List<TrustedDeviceApprovalPending>();
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(
                path,
                new TestProtectionPolicy());
            for (var index = 0; index < 6; index++)
            {
                var pending = Pending(
                    key,
                    $"00000000-0000-0000-0000-{index + 1:D12}",
                    endpoints: WithAlternateEndpoint(endpoints, index));
                pendingEntries.Add(pending);
                using var created = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                    TryCreate(store, pending));
            }

            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                store.RemoveAccountScope("account-1", endpoints));
            foreach (var pending in pendingEntries)
            {
                using var recovered = AssertFound(store.Read(pending.Binding));
                AssertPrivateKeyEquals(key, recovered.Pending);
            }
        }
        finally
        {
            foreach (var pending in pendingEntries)
                pending.Dispose();
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void RemoveAccountScope_DistinguishesAbsentCorruptAndUnavailable()
    {
        var path = NewPath();
        var endpoints = Endpoints();
        try
        {
            var protection = new TestProtectionPolicy();
            var store = new DpapiTrustedDeviceApprovalStore(path, protection);
            Assert.Throws<ArgumentException>(() =>
                store.RemoveAccountScope(" account-1", endpoints));
            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                store.RemoveAccountScope("account-1", endpoints));

            var corrupt = "WVWAPR01corrupt"u8.ToArray();
            try
            {
                File.WriteAllBytes(path, corrupt);
                Assert.Equal(
                    AccountScopedRemovalStatus.Corrupt,
                    store.RemoveAccountScope("account-1", endpoints));
                Assert.Equal(corrupt, File.ReadAllBytes(path));
            }
            finally
            {
                Clear(corrupt);
            }

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.TrustedDeviceApprovalLedger\",\"schemaVersion\":1,\"entries\":[null]}");
            Assert.Equal(
                AccountScopedRemovalStatus.Corrupt,
                store.RemoveAccountScope("account-1", endpoints));

            var unsupportedPath = NewPath();
            try
            {
                var unsupported = new DpapiTrustedDeviceApprovalStore(
                    unsupportedPath,
                    new UnsupportedProtectionPolicy());
                Assert.Equal(
                    AccountScopedRemovalStatus.Unavailable,
                    unsupported.RemoveAccountScope("account-1", endpoints));
                Assert.False(Directory.Exists(Path.GetDirectoryName(unsupportedPath)));
            }
            finally
            {
                DeleteScope(unsupportedPath);
            }
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void RemoveAccountScope_InvalidInnerSecretIsCorruptAndDoesNotRewrite()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var key = NewPrivateKey();
        var endpoints = Endpoints();
        using var target = Pending(key, RequestId, endpoints: endpoints);
        using var other = Pending(
            key,
            OtherRequestId,
            endpoints: WithAlternateApi(endpoints));
        PersistedTrustedDeviceApprovalLedger? ledger = null;
        byte[]? corruptSecret = null;
        byte[]? before = null;
        byte[]? after = null;
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path, protection);
            using var targetRecord = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, target));
            using var otherRecord = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, other));
            ledger = ReadProtectedLedger(path, protection);
            var entries = ledger.Entries.ToArray();
            var otherIndex = Array.FindIndex(
                entries,
                entry => entry.ToEndpoints() == other.Endpoints);
            Assert.True(otherIndex >= 0);
            corruptSecret = [1];
            entries[otherIndex] = entries[otherIndex] with
            {
                ProtectedPrivateKey = corruptSecret,
            };
            var tampered = new PersistedTrustedDeviceApprovalLedger(
                PersistedTrustedDeviceApprovalLedger.FormatName,
                PersistedTrustedDeviceApprovalLedger.CurrentSchemaVersion,
                entries);
            WriteProtectedJson(
                path,
                protection,
                JsonSerializer.Serialize(
                    tampered,
                    AppJsonContext.Default.PersistedTrustedDeviceApprovalLedger));
            before = File.ReadAllBytes(path);

            Assert.Equal(
                AccountScopedRemovalStatus.Corrupt,
                store.RemoveAccountScope(target.AccountId, target.Endpoints));
            after = File.ReadAllBytes(path);
            Assert.Equal(before, after);
            using var recovered = AssertFound(store.Read(target.Binding));
            AssertPrivateKeyEquals(key, recovered.Pending);
        }
        finally
        {
            Clear(after);
            Clear(before);
            Clear(corruptSecret);
            ledger?.ClearSecrets();
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoveAccountScope_RewriteFailureDoesNotReportRemoved(
        bool cryptographicFailure)
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var key = NewPrivateKey();
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path, protection);
            using var created = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, pending));
            if (cryptographicFailure)
                protection.FailNextProtectWithCryptographicException = true;
            else
                protection.FailNextProtectWithIOException = true;

            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(pending.AccountId, pending.Endpoints));
            using var recovered = AssertFound(store.Read(pending.Binding));
            AssertPrivateKeyEquals(key, recovered.Pending);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void RemoveAccountScope_TemporaryCleanupFailureIsRetriedOnAbsent()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(
                path,
                new TestProtectionPolicy());
            using var created = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, pending));
            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                $".{Path.GetFileName(path)}.stale.tmp");
            File.Copy(path, temporaryPath);

            using (var blocker = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                Assert.Equal(
                    AccountScopedRemovalStatus.Unavailable,
                    store.RemoveAccountScope(pending.AccountId, pending.Endpoints));
                Assert.True(File.Exists(temporaryPath));
            }

            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                store.RemoveAccountScope(pending.AccountId, pending.Endpoints));
            Assert.False(File.Exists(temporaryPath));
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                store.Read(pending.Binding).Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceCreate_AllowsOnlyOneRecordPerBinding()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        using var firstPending = Pending(key, RequestId);
        using var secondPending = Pending(key, OtherRequestId);
        var firstStore = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
        var secondStore = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        try
        {
            Task<TrustedDeviceApprovalStoreRecord?> CreateAsync(
                DpapiTrustedDeviceApprovalStore store,
                TrustedDeviceApprovalPending pending) => Task.Run(() =>
            {
                ready.Signal();
                start.Wait(CancellationToken.None);
                return TryCreate(store, pending);
            });

            var first = CreateAsync(firstStore, firstPending);
            var second = CreateAsync(secondStore, secondPending);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            start.Set();
            var results = await Task.WhenAll(first, second);

            var winner = Assert.Single(results, result => result is not null)!;
            winner.Dispose();
            using var persisted = AssertFound(firstStore.Read(firstPending.Binding));
        }
        finally
        {
            start.Set();
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceBeginIntent_AllowsOnlyOneRemoteCreateCriticalSection()
    {
        var path = NewPath();
        var binding = new DeviceKeyBinding("account-1", Endpoints(), DeviceId);
        var firstStore = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
        var secondStore = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        try
        {
            Task<ITrustedDeviceApprovalIntentLease?> AcquireAsync(
                DpapiTrustedDeviceApprovalStore store) => Task.Run(() =>
            {
                ready.Signal();
                start.Wait(CancellationToken.None);
                return store.TryAcquireBeginIntent(binding);
            });

            var first = AcquireAsync(firstStore);
            var second = AcquireAsync(secondStore);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            start.Set();
            var results = await Task.WhenAll(first, second);

            var winner = Assert.Single(results, result => result is not null)!;
            Assert.Equal(binding, winner.Binding);
            winner.Dispose();
            using var reacquired = Assert.IsType<DpapiTrustedDeviceApprovalStore>(secondStore)
                .TryAcquireBeginIntent(binding);
            Assert.NotNull(reacquired);
        }
        finally
        {
            start.Set();
            DeleteScope(path);
        }
    }

    [Fact]
    public void TryCreate_RequiresLiveIntentFromSameStoreAndExactBinding()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        using var pending = Pending(key, RequestId);
        using var other = Pending(
            key,
            OtherRequestId,
            accountId: "account-2",
            deviceId: "bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        try
        {
            var first = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
            var second = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
            var intent = Assert.IsAssignableFrom<ITrustedDeviceApprovalIntentLease>(
                first.TryAcquireBeginIntent(pending.Binding));

            Assert.Throws<ArgumentException>(() => first.TryCreate(intent, other));
            Assert.Throws<ArgumentException>(() => second.TryCreate(intent, pending));
            intent.Dispose();
            Assert.Throws<ObjectDisposedException>(() => first.TryCreate(intent, pending));
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                first.Read(pending.Binding).Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void TryCreate_WhenLocalWriteGuardDeniesThrowsWithoutPersistingLedger()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        var guard = new TrackingLocalWriteGuard(allow: false);
        using var pending = Pending(key, RequestId);
        using var mismatched = Pending(
            key,
            OtherRequestId,
            accountId: "account-2");
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(
                path,
                new TestProtectionPolicy(),
                guard);
            using var intent = Assert.IsAssignableFrom<ITrustedDeviceApprovalIntentLease>(
                store.TryAcquireBeginIntent(pending.Binding));

            Assert.Throws<ArgumentException>(() => store.TryCreate(intent, mismatched));
            Assert.Equal(0, guard.AcquireCount);
            Assert.Throws<AccountDeletionWriteBlockedException>(() =>
                store.TryCreate(intent, pending));

            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(pending.AccountId, guard.LastAccountId);
            Assert.Equal(pending.Endpoints, guard.LastEndpoints);
            Assert.False(guard.IsHeld);
            Assert.False(File.Exists(path));
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                store.Read(pending.Binding).Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void TryCreate_HoldsLocalWriteGuardThroughCommitAndReleasesIt()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        var guard = new TrackingLocalWriteGuard(allow: true);
        var observedProtectedWrites = 0;
        var protection = new TestProtectionPolicy
        {
            BeforeProtect = () =>
            {
                Assert.True(guard.IsHeld);
                observedProtectedWrites++;
            },
        };
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(
                path,
                protection,
                guard);
            using var created = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, pending));

            Assert.True(observedProtectedWrites > 0);
            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(1, guard.DisposeCount);
            Assert.False(guard.IsHeld);
            Assert.True(File.Exists(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void TryCreate_ReleasesLocalWriteGuardWhenProtectionFails()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        var guard = new TrackingLocalWriteGuard(allow: true);
        var protection = new TestProtectionPolicy
        {
            FailNextProtectWithCryptographicException = true,
        };
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(
                path,
                protection,
                guard);
            using var intent = Assert.IsAssignableFrom<ITrustedDeviceApprovalIntentLease>(
                store.TryAcquireBeginIntent(pending.Binding));

            Assert.Throws<CryptographicException>(() => store.TryCreate(intent, pending));

            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(1, guard.DisposeCount);
            Assert.False(guard.IsHeld);
            Assert.False(File.Exists(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void ReadIntentAndCleanupOperations_DoNotAcquireDenyingLocalWriteGuard()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        var protection = new TestProtectionPolicy();
        using var firstPending = Pending(key, RequestId);
        using var secondPending = Pending(
            key,
            OtherRequestId,
            deviceId: OtherDeviceId);
        using var unusedPending = Pending(
            key,
            ThirdRequestId,
            accountId: "account-2");
        try
        {
            var unguarded = new DpapiTrustedDeviceApprovalStore(path, protection);
            using var first = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(unguarded, firstPending));
            using var second = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(unguarded, secondPending));

            var guard = new TrackingLocalWriteGuard(allow: false);
            var guarded = new DpapiTrustedDeviceApprovalStore(
                path,
                protection,
                guard);
            using var recovered = AssertFound(guarded.Read(firstPending.Binding));
            using var intent = Assert.IsAssignableFrom<ITrustedDeviceApprovalIntentLease>(
                guarded.TryAcquireBeginIntent(unusedPending.Binding));

            Assert.True(guarded.Remove(
                first,
                TrustedDeviceApprovalRemovalReason.Cancelled));
            Assert.Equal(
                AccountScopedRemovalStatus.Removed,
                guarded.RemoveAccountScope(
                    secondPending.AccountId,
                    secondPending.Endpoints));
            Assert.Equal(0, guard.AcquireCount);
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                guarded.Read(secondPending.Binding).Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void Read_DistinguishesAbsentCorruptAndUnavailableAndMutationFailsClosed()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        using var pending = Pending(key, RequestId);
        try
        {
            var protection = new TestProtectionPolicy();
            var supported = new DpapiTrustedDeviceApprovalStore(path, protection);
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                supported.Read(pending.Binding).Status);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var corrupt = "WVWAPR01corrupt"u8.ToArray();
            try
            {
                File.WriteAllBytes(path, corrupt);
                Assert.Equal(
                    TrustedDeviceApprovalStoreReadStatus.Corrupt,
                    supported.Read(pending.Binding).Status);
                Assert.ThrowsAny<CryptographicException>(() => TryCreate(supported, pending));
                Assert.Equal(corrupt, File.ReadAllBytes(path));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(corrupt);
            }

            using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write))
                oversized.SetLength(256L * 1024 + 1);
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Corrupt,
                supported.Read(pending.Binding).Status);

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.TrustedDeviceApprovalLedger\",\"schemaVersion\":1,\"entries\":[],\"unexpected\":true}");
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Corrupt,
                supported.Read(pending.Binding).Status);

            var unsupportedPath = NewPath();
            var unsupported = new DpapiTrustedDeviceApprovalStore(
                unsupportedPath,
                new UnsupportedProtectionPolicy());
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Unavailable,
                unsupported.Read(pending.Binding).Status);
            Assert.Throws<PlatformNotSupportedException>(() => TryCreate(unsupported, pending));
            Assert.False(Directory.Exists(Path.GetDirectoryName(unsupportedPath)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [Fact]
    public void FakeProtection_ClearsOwnedBuffersAndPersistedEntrySecret()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        var protection = new TestProtectionPolicy();
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path, protection);
            using var record = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, pending));
            AssertCleared(protection.LastProtectPlaintext);
            AssertCleared(protection.LastProtectEntropy);
            AssertCleared(protection.LastProtectOutput);
            Assert.All(protection.ProtectPlaintexts, AssertCleared);
            Assert.All(protection.ProtectEntropies, AssertCleared);
            Assert.All(protection.ProtectOutputs, AssertCleared);

            using var loaded = AssertFound(store.Read(pending.Binding));
            AssertCleared(protection.LastUnprotectProtectedData);
            AssertCleared(protection.LastUnprotectEntropy);
            AssertCleared(protection.LastUnprotectPlaintext);
            Assert.All(protection.UnprotectProtectedPayloads, AssertCleared);
            Assert.All(protection.UnprotectEntropies, AssertCleared);
            Assert.All(protection.UnprotectPlaintexts, AssertCleared);

            var protectedPayload = Enumerable.Range(1, 64)
                .Select(value => (byte)value)
                .ToArray();
            var entry = PersistedTrustedDeviceApprovalEntry.Create(
                Guid.NewGuid(),
                1,
                pending,
                protectedPayload);
            var ownedSecret = entry.ProtectedPrivateKey;
            entry.ClearSecret();
            AssertCleared(ownedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void WriteThroughReplaceFailure_PreservesExistingRecordAndCleansTemp()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        using var firstPending = Pending(key, RequestId);
        using var secondPending = Pending(
            key,
            OtherRequestId,
            accountId: "account-2",
            deviceId: "bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path, new TestProtectionPolicy());
            using var first = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, firstPending));

            using (var blocker = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                Assert.Throws<IOException>(() => TryCreate(store, secondPending));
            }

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            using var recovered = AssertFound(store.Read(firstPending.Binding));
            Assert.Equal(RequestId, recovered.Pending.RequestId);
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Absent,
                store.Read(secondPending.Binding).Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void CurrentUserDpapi_RoundTripsWithoutPersistingSensitivePlaintext()
    {
        var path = NewPath();
        var key = NewPrivateKey();
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path);
            using var created = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, pending));

            var file = File.ReadAllBytes(path);
            var account = Encoding.UTF8.GetBytes(pending.AccountId);
            var privateKeyBase64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(key));
            try
            {
                Assert.False(ContainsSequence(file, account));
                Assert.False(ContainsSequence(file, privateKeyBase64));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyBase64);
                CryptographicOperations.ZeroMemory(account);
                CryptographicOperations.ZeroMemory(file);
            }

            using var recovered = AssertFound(
                new DpapiTrustedDeviceApprovalStore(path).Read(
                    pending.Binding,
                    RequestId));
            AssertPrivateKeyEquals(key, recovered.Pending);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void CurrentUserDpapi_WrongPurposeEntropyCannotOpenLedger()
    {
        var path = NewPath();
        byte[]? plaintext = null;
        byte[]? domain = null;
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framed = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                PersistedTrustedDeviceApprovalLedger.Empty,
                AppJsonContext.Default.PersistedTrustedDeviceApprovalLedger);
            domain = Encoding.UTF8.GetBytes(
                "WinVaultWarden.TrustedDeviceApprovalLedgerProtection.other");
            entropy = SHA256.HashData(domain);
            protectedBytes = ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);
            framed = new byte[8 + protectedBytes.Length];
            "WVWAPR01"u8.CopyTo(framed);
            protectedBytes.CopyTo(framed, 8);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, framed);

            var binding = new DeviceKeyBinding("account-1", Endpoints(), DeviceId);
            Assert.Equal(
                TrustedDeviceApprovalStoreReadStatus.Corrupt,
                new DpapiTrustedDeviceApprovalStore(path).Read(binding).Status);
        }
        finally
        {
            Clear(framed);
            Clear(protectedBytes);
            Clear(entropy);
            Clear(domain);
            Clear(plaintext);
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void TokenAndCacheClear_DoNotRemoveApprovalLedger()
    {
        var path = NewPath();
        var directory = Path.GetDirectoryName(path)!;
        var key = NewPrivateKey();
        using var pending = Pending(key, RequestId);
        try
        {
            var store = new DpapiTrustedDeviceApprovalStore(path);
            using var created = Assert.IsType<TrustedDeviceApprovalStoreRecord>(
                TryCreate(store, pending));

            new DpapiTokenStore(Path.Combine(directory, "session.bin")).Clear();
            new DpapiVaultCacheStore(Path.Combine(directory, "vault-cache.bin"))
                .ClearVaultCache();

            Assert.True(File.Exists(path));
            using var recovered = AssertFound(store.Read(pending.Binding, RequestId));
            AssertPrivateKeyEquals(key, recovered.Pending);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteScope(path);
        }
    }

    private static TrustedDeviceApprovalPending Pending(
        byte[] privateKey,
        string requestId,
        string accountId = "account-1",
        ServerEndpoints? endpoints = null,
        string deviceId = DeviceId,
        TrustedDeviceApprovalRequestType requestType =
            TrustedDeviceApprovalRequestType.AdminApproval) =>
        TrustedDeviceApprovalPending.CreateWithDefaultLifetime(
            requestId,
            privateKey,
            accountId,
            endpoints ?? Endpoints(),
            deviceId,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"),
            requestType);

    private static TrustedDeviceApprovalStoreRecord? TryCreate(
        DpapiTrustedDeviceApprovalStore store,
        TrustedDeviceApprovalPending pending)
    {
        using var intent = store.TryAcquireBeginIntent(pending.Binding);
        return intent is null ? null : store.TryCreate(intent, pending);
    }

    private static ServerEndpoints Endpoints() =>
        ServerEndpoints.ForSelfHosted("https://vault.example/base");

    private static ServerEndpoints WithAlternateApi(ServerEndpoints endpoints) => new(
        endpoints.WebVault,
        new ServerEndpoint("https://api.example/custom"),
        endpoints.Identity,
        endpoints.Notifications,
        endpoints.Icons,
        endpoints.SendWeb);

    private static ServerEndpoints WithAlternateEndpoint(
        ServerEndpoints endpoints,
        int index) => new(
        index == 0 ? endpoints.WebVault.Append("scope-test") : endpoints.WebVault,
        index == 1 ? endpoints.Api.Append("scope-test") : endpoints.Api,
        index == 2 ? endpoints.Identity.Append("scope-test") : endpoints.Identity,
        index == 3 ? endpoints.Notifications.Append("scope-test") : endpoints.Notifications,
        index == 4 ? endpoints.Icons.Append("scope-test") : endpoints.Icons,
        index == 5 ? endpoints.SendWeb.Append("scope-test") : endpoints.SendWeb);

    private static byte[] NewPrivateKey(int keySize = 2048)
    {
        using var rsa = RSA.Create(keySize);
        return rsa.ExportPkcs8PrivateKey();
    }

    private static void AssertValidRsa2048(byte[] privateKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
        Assert.Equal(privateKey.Length, bytesRead);
        Assert.Equal(2048, rsa.KeySize);
    }

    private static void AssertPrivateKeyEquals(
        byte[] expected,
        TrustedDeviceApprovalPending pending)
    {
        var actual = pending.CopyPrivateKeyPkcs8();
        try
        {
            Assert.Equal(expected, actual);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    private static TrustedDeviceApprovalStoreRecord AssertFound(
        TrustedDeviceApprovalStoreReadResult result)
    {
        Assert.Equal(TrustedDeviceApprovalStoreReadStatus.Found, result.Status);
        return Assert.IsType<TrustedDeviceApprovalStoreRecord>(result.Record);
    }

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "trusted-device-approvals.bin");

    private static void DeleteScope(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void AssertCleared(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        Assert.All(bytes!, value => Assert.Equal(0, value));
    }

    private static void WriteProtectedJson(
        string path,
        TestProtectionPolicy protection,
        string json)
    {
        var plaintext = Encoding.UTF8.GetBytes(json);
        var domain = Encoding.UTF8.GetBytes(
            "WinVaultWarden.TrustedDeviceApprovalLedgerProtection.v1");
        var entropy = SHA256.HashData(domain);
        var protectedBytes = protection.Protect(plaintext, entropy);
        var framed = new byte[8 + protectedBytes.Length];
        try
        {
            "WVWAPR01"u8.CopyTo(framed);
            protectedBytes.CopyTo(framed, 8);
            File.WriteAllBytes(path, framed);
        }
        finally
        {
            Clear(framed);
            Clear(protectedBytes);
            Clear(entropy);
            Clear(domain);
            Clear(plaintext);
        }
    }

    private static PersistedTrustedDeviceApprovalLedger ReadProtectedLedger(
        string path,
        TestProtectionPolicy protection)
    {
        byte[]? file = null;
        byte[]? protectedBytes = null;
        byte[]? domain = null;
        byte[]? entropy = null;
        byte[]? plaintext = null;
        try
        {
            file = File.ReadAllBytes(path);
            Assert.True(file.AsSpan(0, 8).SequenceEqual("WVWAPR01"u8));
            protectedBytes = file.AsSpan(8).ToArray();
            domain = Encoding.UTF8.GetBytes(
                "WinVaultWarden.TrustedDeviceApprovalLedgerProtection.v1");
            entropy = SHA256.HashData(domain);
            plaintext = protection.Unprotect(protectedBytes, entropy);
            return Assert.IsType<PersistedTrustedDeviceApprovalLedger>(
                JsonSerializer.Deserialize(
                    plaintext,
                    AppJsonContext.Default.PersistedTrustedDeviceApprovalLedger));
        }
        finally
        {
            Clear(plaintext);
            Clear(entropy);
            Clear(domain);
            Clear(protectedBytes);
            Clear(file);
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }

    private sealed class UnsupportedProtectionPolicy : ITrustedDeviceApprovalProtectionPolicy
    {
        public bool IsSupported => false;

        public byte[] Protect(byte[] plaintext, byte[] entropy) => throw new InvalidOperationException();

        public byte[] Unprotect(byte[] protectedData, byte[] entropy) => throw new InvalidOperationException();
    }

    private sealed class TestProtectionPolicy : ITrustedDeviceApprovalProtectionPolicy
    {
        public bool IsSupported => true;
        public bool FailNextProtectWithIOException { get; set; }
        public bool FailNextProtectWithCryptographicException { get; set; }
        public Action? BeforeProtect { get; init; }
        public byte[]? LastProtectPlaintext { get; private set; }
        public byte[]? LastProtectEntropy { get; private set; }
        public byte[]? LastProtectOutput { get; private set; }
        public byte[]? LastUnprotectProtectedData { get; private set; }
        public byte[]? LastUnprotectEntropy { get; private set; }
        public byte[]? LastUnprotectPlaintext { get; private set; }
        public List<byte[]> ProtectPlaintexts { get; } = [];
        public List<byte[]> ProtectEntropies { get; } = [];
        public List<byte[]> ProtectOutputs { get; } = [];
        public List<byte[]> UnprotectProtectedPayloads { get; } = [];
        public List<byte[]> UnprotectEntropies { get; } = [];
        public List<byte[]> UnprotectPlaintexts { get; } = [];

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            BeforeProtect?.Invoke();
            if (FailNextProtectWithCryptographicException)
            {
                FailNextProtectWithCryptographicException = false;
                throw new CryptographicException(
                    "Injected approval-ledger protection failure.");
            }
            if (FailNextProtectWithIOException)
            {
                FailNextProtectWithIOException = false;
                throw new IOException("Injected approval-ledger rewrite failure.");
            }

            LastProtectPlaintext = plaintext;
            LastProtectEntropy = entropy;
            ProtectPlaintexts.Add(plaintext);
            ProtectEntropies.Add(entropy);
            var output = new byte[entropy.Length + plaintext.Length];
            entropy.CopyTo(output, 0);
            for (var index = 0; index < plaintext.Length; index++)
                output[entropy.Length + index] = (byte)(plaintext[index] ^ entropy[index % entropy.Length]);
            LastProtectOutput = output;
            ProtectOutputs.Add(output);
            return output;
        }

        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
            LastUnprotectProtectedData = protectedData;
            LastUnprotectEntropy = entropy;
            UnprotectProtectedPayloads.Add(protectedData);
            UnprotectEntropies.Add(entropy);
            if (protectedData.Length <= entropy.Length
                || !CryptographicOperations.FixedTimeEquals(
                    protectedData.AsSpan(0, entropy.Length),
                    entropy))
            {
                throw new CryptographicException("Protection entropy does not match.");
            }

            var plaintext = new byte[protectedData.Length - entropy.Length];
            for (var index = 0; index < plaintext.Length; index++)
                plaintext[index] = (byte)(protectedData[entropy.Length + index] ^ entropy[index % entropy.Length]);
            LastUnprotectPlaintext = plaintext;
            UnprotectPlaintexts.Add(plaintext);
            return plaintext;
        }
    }

    private sealed class TrackingLocalWriteGuard(bool allow)
        : IAccountDeletionLocalWriteGuard
    {
        public int AcquireCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool IsHeld { get; private set; }

        public string? LastAccountId { get; private set; }

        public ServerEndpoints? LastEndpoints { get; private set; }

        public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints)
        {
            AcquireCount++;
            LastAccountId = accountId;
            LastEndpoints = endpoints;
            if (!allow)
                return null;

            Assert.False(IsHeld);
            IsHeld = true;
            return new CallbackDisposable(() =>
            {
                Assert.True(IsHeld);
                IsHeld = false;
                DisposeCount++;
            });
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }
}
