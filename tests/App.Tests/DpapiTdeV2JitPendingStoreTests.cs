using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using App.Services;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Crypto.V2;
using Xunit;

namespace App.Tests;

public sealed class DpapiTdeV2JitPendingStoreTests
{
    private const string AccountId = "11111111-2222-3333-4444-555555555555";
    private const string OtherAccountId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string OrganizationId = "12345678-1234-1234-1234-123456789abc";
    private const string DeviceId = "66666666-7777-8888-9999-aaaaaaaaaaaa";
    private const string OtherDeviceId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";

    [Fact]
    public void PendingModel_OwnsAndValidatesExactGeneratedRecoveryState()
    {
        var pending = NewPending(trustDevice: true);
        var userKey = pending.CopyUserKeyTransport();
        var deviceKey = pending.CopyDeviceKey();
        try
        {
            Assert.NotNull(deviceKey);
            Assert.Equal(TdeV2JitPending.DeviceKeyLength, deviceKey!.Length);
            using (var imported = V2UserKey.ImportTransport(userKey))
            {
                Assert.Equal(pending.AccountKeysRequest.UserKeyId, imported.KeyIdHex);
            }

            CryptographicOperations.ZeroMemory(userKey);
            CryptographicOperations.ZeroMemory(deviceKey);
            var restoredUserKey = pending.CopyUserKeyTransport();
            var restoredDeviceKey = pending.CopyDeviceKey();
            try
            {
                using var imported = V2UserKey.ImportTransport(restoredUserKey);
                Assert.Equal(pending.AccountKeysRequest.UserKeyId, imported.KeyIdHex);
                Assert.Equal(TdeV2JitPending.DeviceKeyLength, restoredDeviceKey!.Length);
            }
            finally
            {
                Clear(restoredDeviceKey);
                Clear(restoredUserKey);
            }

            pending.Dispose();
            Assert.Throws<ObjectDisposedException>(() => pending.CopyUserKeyTransport());
            Assert.Throws<ObjectDisposedException>(() => pending.CopyDeviceKey());
        }
        finally
        {
            pending.Dispose();
            Clear(deviceKey);
            Clear(userKey);
        }
    }

    [Fact]
    public void PendingModel_RejectsMismatchedKeyIdBindingsAndDeviceMaterial()
    {
        using var valid = NewPending(trustDevice: true);
        var userKey = valid.CopyUserKeyTransport();
        var deviceKey = valid.CopyDeviceKey()!;
        try
        {
            var mismatchedRequest = new TdeV2JitAccountKeysRequestSnapshot(
                valid.AccountKeysRequest.PublicKey,
                valid.AccountKeysRequest.EncryptedPrivateKey,
                valid.AccountKeysRequest.AccountKeys,
                new string('0', 32));
            Assert.Throws<ArgumentException>(() => new TdeV2JitPending(
                valid.Binding,
                valid.OrganizationId,
                valid.OrganizationIdentifier,
                valid.CreatedAt,
                userKey,
                mismatchedRequest,
                valid.ResetPasswordKey,
                valid.TrustedDeviceRequest,
                deviceKey));

            Assert.Throws<ArgumentException>(() => new TdeV2JitPending(
                new DeviceKeyBinding("not-a-guid", Endpoints(), DeviceId),
                valid.OrganizationId,
                valid.OrganizationIdentifier,
                valid.CreatedAt,
                userKey,
                valid.AccountKeysRequest,
                valid.ResetPasswordKey,
                valid.TrustedDeviceRequest,
                deviceKey));

            Assert.Throws<ArgumentException>(() => new TdeV2JitPending(
                valid.Binding,
                valid.OrganizationId,
                valid.OrganizationIdentifier,
                valid.CreatedAt,
                userKey,
                valid.AccountKeysRequest,
                valid.ResetPasswordKey,
                valid.TrustedDeviceRequest,
                deviceKey.AsSpan(1)));

            Assert.Throws<ArgumentException>(() => new TdeV2JitPending(
                valid.Binding,
                valid.OrganizationId,
                new string('x', TdeV2JitPending.MaximumOrganizationIdentifierLength + 1),
                valid.CreatedAt,
                userKey,
                valid.AccountKeysRequest,
                valid.ResetPasswordKey,
                valid.TrustedDeviceRequest,
                deviceKey));

            Assert.Throws<ArgumentException>(() => new TdeV2JitPending(
                valid.Binding,
                valid.OrganizationId,
                "invalid\ud800identifier",
                valid.CreatedAt,
                userKey,
                valid.AccountKeysRequest,
                valid.ResetPasswordKey,
                valid.TrustedDeviceRequest,
                deviceKey));
        }
        finally
        {
            Clear(deviceKey);
            Clear(userKey);
        }
    }

    [Fact]
    public void PersistedSchema_SourceGeneratedJsonRoundTrips()
    {
        using var pending = NewPending(trustDevice: true);
        var protectedUser = new byte[] { 1, 2, 3 };
        var protectedDevice = new byte[] { 4, 5, 6 };
        byte[]? json = null;
        try
        {
            var entry = PersistedTdeV2JitPendingEntry.Create(
                Guid.NewGuid(),
                1,
                TdeV2JitPendingStage.Prepared,
                pending,
                protectedUser,
                protectedDevice);
            var ledger = new PersistedTdeV2JitPendingLedger(
                PersistedTdeV2JitPendingLedger.FormatName,
                PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                [entry]);
            json = JsonSerializer.SerializeToUtf8Bytes(
                ledger,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            var roundTrip = JsonSerializer.Deserialize(
                json,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            Assert.NotNull(roundTrip);
            Assert.True(roundTrip!.TryValidate(), Encoding.UTF8.GetString(json));
        }
        finally
        {
            Clear(json);
            Clear(protectedDevice);
            Clear(protectedUser);
        }
    }

    [Fact]
    public void PersistedSchema_WithoutDeviceTrustRoundTrips()
    {
        using var pending = NewPending(trustDevice: false);
        var protectedUser = new byte[] { 1, 2, 3 };
        byte[]? json = null;
        try
        {
            var entry = PersistedTdeV2JitPendingEntry.Create(
                Guid.NewGuid(),
                1,
                TdeV2JitPendingStage.Prepared,
                pending,
                protectedUser,
                protectedDeviceKey: null);
            var ledger = new PersistedTdeV2JitPendingLedger(
                PersistedTdeV2JitPendingLedger.FormatName,
                PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                [entry]);
            json = JsonSerializer.SerializeToUtf8Bytes(
                ledger,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            var roundTrip = JsonSerializer.Deserialize(
                json,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            Assert.NotNull(roundTrip);
            Assert.True(roundTrip!.TryValidate(), Encoding.UTF8.GetString(json));
        }
        finally
        {
            Clear(json);
            Clear(protectedUser);
        }
    }

    [Fact]
    public void PersistedSchema_RejectsNullDeviceCiphertextWithoutThrowing()
    {
        using var pending = NewPending(trustDevice: false);
        var protectedUser = new byte[] { 1, 2, 3 };
        try
        {
            var valid = PersistedTdeV2JitPendingEntry.Create(
                Guid.NewGuid(),
                1,
                TdeV2JitPendingStage.Prepared,
                pending,
                protectedUser,
                protectedDeviceKey: null);
            var corrupt = valid with { ProtectedDeviceKey = null! };
            Assert.False(corrupt.TryValidate());
        }
        finally
        {
            Clear(protectedUser);
        }
    }

    [Fact]
    public void FakeProtection_RestoresExactRequestAndSecretsAcrossInstances()
    {
        var path = NewPath();
        using var pending = NewPending(trustDevice: true);
        using var other = NewPending(
            trustDevice: false,
            accountId: OtherAccountId,
            deviceId: OtherDeviceId,
            endpoints: WithAlternateApi(Endpoints()));
        try
        {
            var protection = new TestProtectionPolicy();
            var first = new DpapiTdeV2JitPendingStore(path, protection);
            using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(
                first.TryCreate(pending));
            Assert.Equal(TdeV2JitPendingStage.Prepared, created.Stage);
            Assert.Equal(1, created.Revision);
            Assert.Null(first.TryCreate(pending));
            using var otherCreated = Assert.IsType<TdeV2JitPendingStoreRecord>(
                first.TryCreate(other));

            var reopened = new DpapiTdeV2JitPendingStore(path, protection);
            using var recovered = AssertFound(reopened.Read(pending.Binding));
            Assert.Equal(pending.AccountKeysRequest, recovered.Pending.AccountKeysRequest);
            Assert.Equal(pending.ResetPasswordKey, recovered.Pending.ResetPasswordKey);
            Assert.Equal(pending.OrganizationId, recovered.Pending.OrganizationId);
            Assert.Equal(pending.OrganizationIdentifier, recovered.Pending.OrganizationIdentifier);
            Assert.Equal(pending.TrustedDeviceRequest, recovered.Pending.TrustedDeviceRequest);
            AssertSecretsEqual(pending, recovered.Pending);

            using var recoveredOther = AssertFound(reopened.Read(other.Binding));
            Assert.False(recoveredOther.Pending.TrustCurrentDevice);
            Assert.Null(recoveredOther.Pending.CopyDeviceKey());
            AssertSecretsEqual(other, recoveredOther.Pending);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void AdvanceAndRemove_UseExactMonotonicCas()
    {
        var path = NewPath();
        using var pending = NewPending(trustDevice: true);
        try
        {
            var first = new DpapiTdeV2JitPendingStore(path, new TestProtectionPolicy());
            var second = new DpapiTdeV2JitPendingStore(path, new TestProtectionPolicy());
            var created = Assert.IsType<TdeV2JitPendingStoreRecord>(first.TryCreate(pending));
            using var stale = AssertFound(second.Read(pending.Binding));

            Assert.Throws<ArgumentException>(() =>
                first.TryAdvance(created, TdeV2JitPendingStage.Prepared));
            var started = Assert.IsType<TdeV2JitPendingStoreRecord>(
                first.TryAdvance(created, TdeV2JitPendingStage.AccountKeysSubmissionStarted));
            Assert.Throws<ObjectDisposedException>(() => created.Pending.CopyUserKeyTransport());
            Assert.Equal(2, started.Revision);
            Assert.Null(second.TryAdvance(stale, TdeV2JitPendingStage.AccountKeysConfirmed));
            AssertSecretsEqual(pending, stale.Pending);
            Assert.False(first.Remove(started, TdeV2JitPendingRemovalReason.Completed));
            AssertSecretsEqual(pending, started.Pending);

            var confirmed = Assert.IsType<TdeV2JitPendingStoreRecord>(
                first.TryAdvance(started, TdeV2JitPendingStage.AccountKeysConfirmed));
            var localReady = Assert.IsType<TdeV2JitPendingStoreRecord>(
                first.TryAdvance(confirmed, TdeV2JitPendingStage.LocalCommitReady));
            Assert.True(first.Remove(localReady, TdeV2JitPendingRemovalReason.Completed));
            Assert.Throws<ObjectDisposedException>(() => localReady.Pending.CopyUserKeyTransport());
            Assert.Equal(TdeV2JitPendingReadStatus.Absent, first.Read(pending.Binding).Status);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void ExplicitAbandon_RemovesOnlyExactCurrentRecord()
    {
        var path = NewPath();
        using var pending = NewPending(trustDevice: false);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path, new TestProtectionPolicy());
            var current = Assert.IsType<TdeV2JitPendingStoreRecord>(store.TryCreate(pending));
            using var wrong = new TdeV2JitPendingStoreRecord(
                Guid.NewGuid(),
                current.Revision,
                current.Stage,
                pending.Clone());

            Assert.False(store.Remove(wrong, TdeV2JitPendingRemovalReason.ExplicitlyAbandoned));
            Assert.True(store.Remove(current, TdeV2JitPendingRemovalReason.ExplicitlyAbandoned));
            Assert.Equal(TdeV2JitPendingReadStatus.Absent, store.Read(pending.Binding).Status);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void RemoveAccountScope_RemovesEveryDeviceAndPreservesOtherScopes()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var endpoints = Endpoints();
        var alternateEndpoints = WithAlternateApi(endpoints);
        using var first = NewPending(
            trustDevice: true,
            endpoints: endpoints);
        using var second = NewPending(
            trustDevice: false,
            deviceId: OtherDeviceId,
            endpoints: endpoints);
        using var otherEndpoint = NewPending(
            trustDevice: true,
            endpoints: alternateEndpoints);
        using var otherAccount = NewPending(
            trustDevice: false,
            accountId: OtherAccountId,
            endpoints: endpoints);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path, protection);
            using var firstRecord = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(first));
            using var secondRecord = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(second));
            using var otherEndpointRecord = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(otherEndpoint));
            using var otherAccountRecord = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(otherAccount));

            Assert.Equal(
                AccountScopedRemovalStatus.Removed,
                store.RemoveAccountScope(AccountId, endpoints));

            var reopened = new DpapiTdeV2JitPendingStore(path, protection);
            Assert.Equal(TdeV2JitPendingReadStatus.Absent, reopened.Read(first.Binding).Status);
            Assert.Equal(TdeV2JitPendingReadStatus.Absent, reopened.Read(second.Binding).Status);
            using var preservedEndpoint = AssertFound(reopened.Read(otherEndpoint.Binding));
            using var preservedAccount = AssertFound(reopened.Read(otherAccount.Binding));
            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                reopened.RemoveAccountScope(AccountId, endpoints));
            Assert.All(protection.ProtectPlaintexts, AssertCleared);
            Assert.All(protection.ProtectEntropies, AssertCleared);
            Assert.All(protection.ProtectOutputs, AssertCleared);
            Assert.All(protection.UnprotectProtectedPayloads, AssertCleared);
            Assert.All(protection.UnprotectEntropies, AssertCleared);
            Assert.All(protection.UnprotectPlaintexts, AssertCleared);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void RemoveAccountScope_RequiresAllSixEndpointsToMatch()
    {
        var path = NewPath();
        var endpoints = Endpoints();
        var pendingEntries = new List<TdeV2JitPending>();
        try
        {
            var store = new DpapiTdeV2JitPendingStore(
                path,
                new TestProtectionPolicy());
            for (var index = 0; index < 6; index++)
            {
                var pending = NewPending(
                    trustDevice: index % 2 == 0,
                    endpoints: WithAlternateEndpoint(endpoints, index));
                pendingEntries.Add(pending);
                using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(
                    store.TryCreate(pending));
            }

            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                store.RemoveAccountScope(AccountId, endpoints));
            foreach (var pending in pendingEntries)
            {
                using var recovered = AssertFound(store.Read(pending.Binding));
                AssertSecretsEqual(pending, recovered.Pending);
            }
        }
        finally
        {
            foreach (var pending in pendingEntries)
                pending.Dispose();
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
            var store = new DpapiTdeV2JitPendingStore(path, protection);
            Assert.Throws<ArgumentException>(() =>
                store.RemoveAccountScope("not-a-guid", endpoints));
            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                store.RemoveAccountScope(AccountId, endpoints));

            var corrupt = "WVWTJ201corrupt"u8.ToArray();
            try
            {
                File.WriteAllBytes(path, corrupt);
                Assert.Equal(
                    AccountScopedRemovalStatus.Corrupt,
                    store.RemoveAccountScope(AccountId, endpoints));
                Assert.Equal(corrupt, File.ReadAllBytes(path));
            }
            finally
            {
                Clear(corrupt);
            }

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.TdeV2JitPendingLedger\",\"schemaVersion\":1,\"entries\":[null]}");
            Assert.Equal(
                AccountScopedRemovalStatus.Corrupt,
                store.RemoveAccountScope(AccountId, endpoints));

            var unsupportedPath = NewPath();
            try
            {
                var unsupported = new DpapiTdeV2JitPendingStore(
                    unsupportedPath,
                    new UnsupportedProtectionPolicy());
                Assert.Equal(
                    AccountScopedRemovalStatus.Unavailable,
                    unsupported.RemoveAccountScope(AccountId, endpoints));
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
        var endpoints = Endpoints();
        using var target = NewPending(
            trustDevice: true,
            endpoints: endpoints);
        using var other = NewPending(
            trustDevice: false,
            accountId: OtherAccountId,
            endpoints: WithAlternateApi(endpoints));
        PersistedTdeV2JitPendingLedger? ledger = null;
        byte[]? corruptSecret = null;
        byte[]? before = null;
        byte[]? after = null;
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path, protection);
            using var targetRecord = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(target));
            using var otherRecord = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(other));
            ledger = ReadProtectedLedger(path, protection);
            var entries = ledger.Entries.ToArray();
            var otherIndex = Array.FindIndex(
                entries,
                entry => entry.ToEndpoints() == other.Endpoints);
            Assert.True(otherIndex >= 0);
            corruptSecret = [1];
            entries[otherIndex] = entries[otherIndex] with
            {
                ProtectedUserKeyTransport = corruptSecret,
            };
            var tampered = new PersistedTdeV2JitPendingLedger(
                PersistedTdeV2JitPendingLedger.FormatName,
                PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                entries);
            WriteProtectedJson(
                path,
                protection,
                JsonSerializer.Serialize(
                    tampered,
                    AppJsonContext.Default.PersistedTdeV2JitPendingLedger));
            before = File.ReadAllBytes(path);

            Assert.Equal(
                AccountScopedRemovalStatus.Corrupt,
                store.RemoveAccountScope(target.AccountId, target.Endpoints));
            after = File.ReadAllBytes(path);
            Assert.Equal(before, after);
            using var recovered = AssertFound(store.Read(target.Binding));
            AssertSecretsEqual(target, recovered.Pending);
        }
        finally
        {
            Clear(after);
            Clear(before);
            Clear(corruptSecret);
            ledger?.ClearSecrets();
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
        using var pending = NewPending(trustDevice: true);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path, protection);
            using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(pending));
            if (cryptographicFailure)
                protection.FailNextProtectWithCryptographicException = true;
            else
                protection.FailNextProtectWithIOException = true;

            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(pending.AccountId, pending.Endpoints));
            using var recovered = AssertFound(store.Read(pending.Binding));
            AssertSecretsEqual(pending, recovered.Pending);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void RemoveAccountScope_TemporaryCleanupFailureIsRetriedOnAbsent()
    {
        var path = NewPath();
        using var pending = NewPending(trustDevice: true);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(
                path,
                new TestProtectionPolicy());
            using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(pending));
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
                TdeV2JitPendingReadStatus.Absent,
                store.Read(pending.Binding).Status);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceCreate_AllowsOnlyOneRecordPerBinding()
    {
        var path = NewPath();
        using var firstPending = NewPending(trustDevice: true);
        using var secondPending = NewPending(trustDevice: true);
        var firstStore = new DpapiTdeV2JitPendingStore(path, new TestProtectionPolicy());
        var secondStore = new DpapiTdeV2JitPendingStore(path, new TestProtectionPolicy());
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        try
        {
            Task<TdeV2JitPendingStoreRecord?> CreateAsync(
                DpapiTdeV2JitPendingStore store,
                TdeV2JitPending pending) => Task.Run(() =>
            {
                ready.Signal();
                start.Wait(CancellationToken.None);
                return store.TryCreate(pending);
            });

            var first = CreateAsync(firstStore, firstPending);
            var second = CreateAsync(secondStore, secondPending);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            start.Set();
            var results = await Task.WhenAll(first, second);

            using var winner = Assert.Single(results, result => result is not null)!;
            using var recovered = AssertFound(firstStore.Read(firstPending.Binding));
            Assert.True(
                recovered.Pending.AccountKeysRequest == firstPending.AccountKeysRequest
                || recovered.Pending.AccountKeysRequest == secondPending.AccountKeysRequest);
        }
        finally
        {
            start.Set();
            DeleteScope(path);
        }
    }

    [Fact]
    public void Read_DistinguishesAbsentCorruptAndUnavailableAndMutationFailsClosed()
    {
        var path = NewPath();
        using var pending = NewPending(trustDevice: true);
        try
        {
            var protection = new TestProtectionPolicy();
            var store = new DpapiTdeV2JitPendingStore(path, protection);
            Assert.Equal(TdeV2JitPendingReadStatus.Absent, store.Read(pending.Binding).Status);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var corrupt = "WVWTJ201corrupt"u8.ToArray();
            try
            {
                File.WriteAllBytes(path, corrupt);
                Assert.Equal(TdeV2JitPendingReadStatus.Corrupt, store.Read(pending.Binding).Status);
                Assert.ThrowsAny<CryptographicException>(() => store.TryCreate(pending));
                Assert.Equal(corrupt, File.ReadAllBytes(path));
            }
            finally
            {
                Clear(corrupt);
            }

            using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write))
                oversized.SetLength(2L * 1024 * 1024 + 1);
            Assert.Equal(TdeV2JitPendingReadStatus.Corrupt, store.Read(pending.Binding).Status);

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.TdeV2JitPendingLedger\",\"schemaVersion\":1,\"entries\":[],\"unexpected\":true}");
            Assert.Equal(TdeV2JitPendingReadStatus.Corrupt, store.Read(pending.Binding).Status);

            var unsupportedPath = NewPath();
            var unsupported = new DpapiTdeV2JitPendingStore(
                unsupportedPath,
                new UnsupportedProtectionPolicy());
            Assert.Equal(
                TdeV2JitPendingReadStatus.Unavailable,
                unsupported.Read(pending.Binding).Status);
            Assert.Throws<PlatformNotSupportedException>(() => unsupported.TryCreate(pending));
            Assert.False(Directory.Exists(Path.GetDirectoryName(unsupportedPath)));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void FakeProtection_ClearsEveryOwnedSecretBuffer()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        using var pending = NewPending(trustDevice: true);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path, protection);
            using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(store.TryCreate(pending));
            Assert.All(protection.ProtectPlaintexts, AssertCleared);
            Assert.All(protection.ProtectEntropies, AssertCleared);
            Assert.All(protection.ProtectOutputs, AssertCleared);

            using var loaded = AssertFound(store.Read(pending.Binding));
            Assert.All(protection.UnprotectProtectedPayloads, AssertCleared);
            Assert.All(protection.UnprotectEntropies, AssertCleared);
            Assert.All(protection.UnprotectPlaintexts, AssertCleared);

            var protectedUserKey = new byte[] { 1, 2, 3 };
            var protectedDeviceKey = new byte[] { 4, 5, 6 };
            var entry = PersistedTdeV2JitPendingEntry.Create(
                Guid.NewGuid(),
                revision: 1,
                TdeV2JitPendingStage.Prepared,
                pending,
                protectedUserKey,
                protectedDeviceKey);
            entry.ClearSecrets();
            AssertCleared(protectedUserKey);
            AssertCleared(protectedDeviceKey);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void InnerProtection_BindsSecretsToImmutableRecoveryContext()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        using var pending = NewPending(trustDevice: true);
        PersistedTdeV2JitPendingLedger? ledger = null;
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path, protection);
            using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(store.TryCreate(pending));
            ledger = ReadProtectedLedger(path, protection);
            var entry = Assert.Single(ledger.Entries);
            var tampered = new PersistedTdeV2JitPendingLedger(
                PersistedTdeV2JitPendingLedger.FormatName,
                PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                [entry with { OrganizationIdentifier = "tampered-sso" }]);
            var json = JsonSerializer.Serialize(
                tampered,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            WriteProtectedJson(path, protection, json);

            Assert.Equal(
                TdeV2JitPendingReadStatus.Corrupt,
                store.Read(pending.Binding).Status);
        }
        finally
        {
            ledger?.ClearSecrets();
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void WriteThroughReplaceFailure_PreservesExistingRecordAndCleansTemp()
    {
        var path = NewPath();
        using var firstPending = NewPending(trustDevice: true);
        using var secondPending = NewPending(
            trustDevice: false,
            accountId: OtherAccountId,
            deviceId: OtherDeviceId);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path, new TestProtectionPolicy());
            using var first = Assert.IsType<TdeV2JitPendingStoreRecord>(
                store.TryCreate(firstPending));

            using (var blocker = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                Assert.Throws<IOException>(() => store.TryCreate(secondPending));
            }

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            using var recovered = AssertFound(store.Read(firstPending.Binding));
            AssertSecretsEqual(firstPending, recovered.Pending);
            Assert.Equal(
                TdeV2JitPendingReadStatus.Absent,
                store.Read(secondPending.Binding).Status);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void CurrentUserDpapi_RoundTripsWithoutPersistingSensitivePlaintext()
    {
        var path = NewPath();
        using var pending = NewPending(trustDevice: true);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path);
            using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(store.TryCreate(pending));

            var file = File.ReadAllBytes(path);
            var account = Encoding.UTF8.GetBytes(pending.AccountId);
            var userKey = pending.CopyUserKeyTransport();
            var deviceKey = pending.CopyDeviceKey();
            try
            {
                Assert.False(ContainsSequence(file, account));
                Assert.False(ContainsSequence(file, userKey));
                Assert.False(ContainsSequence(file, deviceKey!));
            }
            finally
            {
                Clear(deviceKey);
                Clear(userKey);
                Clear(account);
                Clear(file);
            }

            using var recovered = AssertFound(
                new DpapiTdeV2JitPendingStore(path).Read(pending.Binding));
            AssertSecretsEqual(pending, recovered.Pending);
            Assert.Equal(pending.AccountKeysRequest, recovered.Pending.AccountKeysRequest);
        }
        finally
        {
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
                PersistedTdeV2JitPendingLedger.Empty,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            domain = Encoding.UTF8.GetBytes(
                "WinVaultWarden.TdeV2JitPendingLedgerProtection.other");
            entropy = SHA256.HashData(domain);
            protectedBytes = ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);
            framed = new byte[8 + protectedBytes.Length];
            "WVWTJ201"u8.CopyTo(framed);
            protectedBytes.CopyTo(framed, 8);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, framed);

            using var pending = NewPending(trustDevice: false);
            Assert.Equal(
                TdeV2JitPendingReadStatus.Corrupt,
                new DpapiTdeV2JitPendingStore(path).Read(pending.Binding).Status);
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
    public void TokenAndCacheClear_DoNotRemoveV2JitPendingLedger()
    {
        var path = NewPath();
        var directory = Path.GetDirectoryName(path)!;
        using var pending = NewPending(trustDevice: true);
        try
        {
            var store = new DpapiTdeV2JitPendingStore(path);
            using var created = Assert.IsType<TdeV2JitPendingStoreRecord>(store.TryCreate(pending));

            new DpapiTokenStore(Path.Combine(directory, "session.bin")).Clear();
            new DpapiVaultCacheStore(Path.Combine(directory, "vault-cache.bin"))
                .ClearVaultCache();

            Assert.True(File.Exists(path));
            using var recovered = AssertFound(store.Read(pending.Binding));
            AssertSecretsEqual(pending, recovered.Pending);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void AccountDeletionGuard_BlocksUpsertsButAllowsExplicitCleanup()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        using var pending = NewPending(trustDevice: true);
        var denied = new DpapiTdeV2JitPendingStore(
            path,
            protection,
            new DenyingWriteGuard());
        TdeV2JitPendingStoreRecord? created = null;
        try
        {
            Assert.Throws<AccountDeletionWriteBlockedException>(() =>
                denied.TryCreate(pending));
            Assert.False(File.Exists(path));

            var allowed = new DpapiTdeV2JitPendingStore(path, protection);
            created = Assert.IsType<TdeV2JitPendingStoreRecord>(allowed.TryCreate(pending));
            Assert.Throws<AccountDeletionWriteBlockedException>(() =>
                denied.TryAdvance(created, TdeV2JitPendingStage.AccountKeysSubmissionStarted));

            Assert.True(denied.Remove(
                created,
                TdeV2JitPendingRemovalReason.ExplicitlyAbandoned));
            created = null;
            Assert.Equal(TdeV2JitPendingReadStatus.Absent, denied.Read(pending.Binding).Status);
        }
        finally
        {
            created?.Dispose();
            DeleteScope(path);
        }
    }

    private static TdeV2JitPending NewPending(
        bool trustDevice,
        string accountId = AccountId,
        string deviceId = DeviceId,
        ServerEndpoints? endpoints = null)
    {
        using var organizationKey = RSA.Create(2048);
        var organizationPublicKey = organizationKey.ExportSubjectPublicKeyInfo();
        byte[]? userKeyTransport = null;
        byte[]? deviceKey = null;
        try
        {
            using var material = V2TdeRegistrationMaterial.Create(
                organizationPublicKey,
                trustDevice);
            userKeyTransport = material.AccountKeys.UserKey.ExportTransport();
            deviceKey = material.TrustedDeviceKeys?.ExportDeviceKey();
            var accountKeys = material.AccountKeys;
            var request = new TdeV2JitAccountKeysRequestSnapshot(
                accountKeys.AccountPublicKey,
                accountKeys.WrappedPrivateKey,
                new TdeV2JitAccountKeysSnapshot(
                    accountKeys.WrappedPrivateKey,
                    accountKeys.AccountPublicKey,
                    new TdeV2JitPublicKeyEncryptionKeyPairSnapshot(
                        accountKeys.WrappedPrivateKey,
                        accountKeys.AccountPublicKey,
                        accountKeys.SignedPublicKey),
                    new TdeV2JitSignatureKeyPairSnapshot(
                        accountKeys.SignatureAlgorithm,
                        accountKeys.WrappedSigningKey,
                        accountKeys.VerifyingKey),
                    new TdeV2JitAccountSecurityStateSnapshot(
                        accountKeys.SecurityState,
                        accountKeys.SecurityVersion)),
                accountKeys.UserKey.KeyIdHex);
            var trusted = material.TrustedDeviceKeys is null
                ? null
                : new TdeV2JitTrustedDeviceRequestSnapshot(
                    material.TrustedDeviceKeys.EncryptedUserKey,
                    material.TrustedDeviceKeys.EncryptedPublicKey,
                    material.TrustedDeviceKeys.EncryptedPrivateKey);
            return new TdeV2JitPending(
                new DeviceKeyBinding(accountId, endpoints ?? Endpoints(), deviceId),
                OrganizationId,
                "enterprise-sso",
                DateTimeOffset.Parse("2026-08-31T00:00:00Z"),
                userKeyTransport,
                request,
                material.ResetPasswordKey,
                trusted,
                deviceKey is null ? ReadOnlySpan<byte>.Empty : deviceKey);
        }
        finally
        {
            Clear(deviceKey);
            Clear(userKeyTransport);
            Clear(organizationPublicKey);
        }
    }

    private sealed class DenyingWriteGuard : IAccountDeletionLocalWriteGuard
    {
        public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints) => null;
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

    private static void AssertSecretsEqual(TdeV2JitPending expected, TdeV2JitPending actual)
    {
        var expectedUserKey = expected.CopyUserKeyTransport();
        var actualUserKey = actual.CopyUserKeyTransport();
        var expectedDeviceKey = expected.CopyDeviceKey();
        var actualDeviceKey = actual.CopyDeviceKey();
        try
        {
            Assert.Equal(expectedUserKey, actualUserKey);
            Assert.Equal(expectedDeviceKey, actualDeviceKey);
        }
        finally
        {
            Clear(actualDeviceKey);
            Clear(expectedDeviceKey);
            Clear(actualUserKey);
            Clear(expectedUserKey);
        }
    }

    private static TdeV2JitPendingStoreRecord AssertFound(TdeV2JitPendingReadResult result)
    {
        Assert.Equal(TdeV2JitPendingReadStatus.Found, result.Status);
        return Assert.IsType<TdeV2JitPendingStoreRecord>(result.Record);
    }

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "tde-v2-jit-pending.bin");

    private static void DeleteScope(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void AssertCleared(byte[] bytes) =>
        Assert.All(bytes, value => Assert.Equal(0, value));

    private static void WriteProtectedJson(
        string path,
        TestProtectionPolicy protection,
        string json)
    {
        var plaintext = Encoding.UTF8.GetBytes(json);
        var domain = Encoding.UTF8.GetBytes(
            "WinVaultWarden.TdeV2JitPendingLedgerProtection.v1");
        var entropy = SHA256.HashData(domain);
        var protectedBytes = protection.Protect(plaintext, entropy);
        var framed = new byte[8 + protectedBytes.Length];
        try
        {
            "WVWTJ201"u8.CopyTo(framed);
            protectedBytes.CopyTo(framed, 8);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static PersistedTdeV2JitPendingLedger ReadProtectedLedger(
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
            Assert.True(file.AsSpan(0, 8).SequenceEqual("WVWTJ201"u8));
            protectedBytes = file.AsSpan(8).ToArray();
            domain = Encoding.UTF8.GetBytes(
                "WinVaultWarden.TdeV2JitPendingLedgerProtection.v1");
            entropy = SHA256.HashData(domain);
            plaintext = protection.Unprotect(protectedBytes, entropy);
            return Assert.IsType<PersistedTdeV2JitPendingLedger>(
                JsonSerializer.Deserialize(
                    plaintext,
                    AppJsonContext.Default.PersistedTdeV2JitPendingLedger));
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

    private sealed class UnsupportedProtectionPolicy : ITdeV2JitPendingProtectionPolicy
    {
        public bool IsSupported => false;

        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new InvalidOperationException();

        public byte[] Unprotect(byte[] protectedData, byte[] entropy) =>
            throw new InvalidOperationException();
    }

    private sealed class TestProtectionPolicy : ITdeV2JitPendingProtectionPolicy
    {
        public bool IsSupported => true;
        public bool FailNextProtectWithIOException { get; set; }
        public bool FailNextProtectWithCryptographicException { get; set; }
        public List<byte[]> ProtectPlaintexts { get; } = [];
        public List<byte[]> ProtectEntropies { get; } = [];
        public List<byte[]> ProtectOutputs { get; } = [];
        public List<byte[]> UnprotectProtectedPayloads { get; } = [];
        public List<byte[]> UnprotectEntropies { get; } = [];
        public List<byte[]> UnprotectPlaintexts { get; } = [];

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            if (FailNextProtectWithCryptographicException)
            {
                FailNextProtectWithCryptographicException = false;
                throw new CryptographicException(
                    "Injected V2 JIT ledger protection failure.");
            }
            if (FailNextProtectWithIOException)
            {
                FailNextProtectWithIOException = false;
                throw new IOException("Injected V2 JIT ledger rewrite failure.");
            }

            ProtectPlaintexts.Add(plaintext);
            ProtectEntropies.Add(entropy);
            var output = new byte[entropy.Length + plaintext.Length];
            entropy.CopyTo(output, 0);
            for (var index = 0; index < plaintext.Length; index++)
                output[entropy.Length + index] = (byte)(plaintext[index] ^ entropy[index % entropy.Length]);
            ProtectOutputs.Add(output);
            return output;
        }

        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
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
            UnprotectPlaintexts.Add(plaintext);
            return plaintext;
        }
    }
}
