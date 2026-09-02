using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using App.Services;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class DpapiCipherCreatePendingStoreTests
{
    [Fact]
    public void Read_DistinguishesAbsentCorruptAndUnavailable()
    {
        var path = NewPath();
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/base");
        try
        {
            var supported = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());
            Assert.Equal(
                CipherCreatePendingReadStatus.Absent,
                supported.Read("account-1", endpoints).Status);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, "not-a-ledger"u8.ToArray());
            Assert.Equal(
                CipherCreatePendingReadStatus.Corrupt,
                supported.Read("account-1", endpoints).Status);

            var unavailablePath = NewPath();
            var unavailable = new DpapiCipherCreatePendingStore(
                unavailablePath,
                new UnsupportedProtectionPolicy());
            Assert.Equal(
                CipherCreatePendingReadStatus.Unavailable,
                unavailable.Read("account-1", endpoints).Status);
            Assert.False(Directory.Exists(Path.GetDirectoryName(unavailablePath)));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void CorruptLedger_BlocksMutationWithoutOverwritingEvidence()
    {
        var path = NewPath();
        var bytes = "WVWCPD01corrupt"u8.ToArray();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            var store = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());

            Assert.ThrowsAny<CryptographicException>(() => store.TryCreate(Pending('A')));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            DeleteScope(path);
        }
    }

    [Fact]
    public void FakeProtection_RoundTripsAcrossInstancesAndUpdatesMonotonically()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var initial = Pending('A');
        var alternateEndpoints = WithAlternateApi(initial.Witness.Endpoints);
        var alternate = Pending('B', alternateEndpoints);
        try
        {
            var first = new DpapiCipherCreatePendingStore(path, protection);
            using var firstReservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                first.TryCreate(initial));
            Assert.Null(first.TryCreate(Pending('C')));
            using var alternateReservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                first.TryCreate(alternate));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));

            var reopened = new DpapiCipherCreatePendingStore(path, protection);
            var recoveredRecord = AssertFoundRecord(reopened.Read(
                initial.Witness.AccountId,
                initial.Witness.Endpoints));
            var recovered = recoveredRecord.Pending;
            Assert.Equal(initial.Witness.KeyFingerprint, recovered.Witness.KeyFingerprint);

            var committed = initial with
            {
                CommitConfirmed = true,
                ExpectedCipherId = "server-id-1",
            };
            var upgraded = Assert.IsType<CipherCreatePendingRecord>(reopened.TryUpdate(
                recoveredRecord,
                committed));
            Assert.True(upgraded.Pending.CommitConfirmed);
            Assert.Equal("server-id-1", upgraded.Pending.ExpectedCipherId);
            Assert.True(upgraded.Revision > recoveredRecord.Revision);

            Assert.Null(reopened.TryUpdate(recoveredRecord, initial));
            var stale = Assert.IsType<CipherCreatePendingRecord>(reopened.TryUpdate(
                upgraded,
                initial));
            Assert.True(stale.Pending.CommitConfirmed);
            Assert.Equal("server-id-1", stale.Pending.ExpectedCipherId);
            Assert.Throws<InvalidDataException>(() => reopened.TryUpdate(
                upgraded,
                committed with { ExpectedCipherId = "server-id-2" }));

            Assert.False(reopened.RemoveConfirmed(recoveredRecord));
            Assert.True(reopened.RemoveConfirmed(upgraded));
            Assert.Equal(
                CipherCreatePendingReadStatus.Absent,
                reopened.Read(initial.Witness.AccountId, initial.Witness.Endpoints).Status);
            Assert.Equal(
                CipherCreatePendingReadStatus.Found,
                reopened.Read(alternate.Witness.AccountId, alternateEndpoints).Status);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceReservation_AllowsOnlyOneEntryPerCompleteScope()
    {
        var path = NewPath();
        var first = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());
        var second = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        try
        {
            async Task<ICipherCreatePendingReservation?> ReserveAsync(
                DpapiCipherCreatePendingStore store,
                char fingerprint)
            {
                return await Task.Run(() =>
                {
                    ready.Signal();
                    start.Wait(CancellationToken.None);
                    return store.TryCreate(Pending(fingerprint));
                }, CancellationToken.None);
            }

            var firstAttempt = ReserveAsync(first, 'A');
            var secondAttempt = ReserveAsync(second, 'B');
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            start.Set();
            var results = await Task.WhenAll(firstAttempt, secondAttempt);

            var winner = Assert.Single(results, result => result is not null)!;
            var recovered = AssertFound(first.Read(
                "account-1",
                ServerEndpoints.ForSelfHosted("https://vault.example/base")));
            Assert.Contains(recovered.Witness.KeyFingerprint[0], new[] { 'A', 'B' });
            winner.Dispose();
        }
        finally
        {
            start.Set();
            DeleteScope(path);
        }
    }

    [Fact]
    public void AbandonAndConfirmedRemoval_RequireCurrentRevisionAndTerminalState()
    {
        var path = NewPath();
        var store = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());
        var pending = Pending('A');
        try
        {
            using var reservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                store.TryCreate(pending));
            Assert.False(store.Abandon(reservation.Record));

            var released = Assert.IsType<CipherCreatePendingRecord>(store.Release(
                reservation.Record,
                pending));
            reservation.Dispose();
            Assert.False(released.IsInFlight);

            var terminal = pending with
            {
                CommitConfirmed = true,
                ExpectedCipherId = "server-id-1",
            };
            var upgraded = Assert.IsType<CipherCreatePendingRecord>(store.TryUpdate(
                released,
                terminal));

            Assert.False(store.Abandon(released));
            Assert.False(store.RemoveConfirmed(released));
            Assert.True(store.RemoveConfirmed(upgraded));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void NewReservation_CannotLeaveGhostWhilePreviousScopeLeaseIsStillHeld()
    {
        var path = NewPath();
        var first = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());
        var second = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());
        var pending = Pending('A');
        try
        {
            using var firstReservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                first.TryCreate(pending));
            var terminal = pending with
            {
                CommitConfirmed = true,
                ExpectedCipherId = "server-id-1",
            };
            var upgraded = Assert.IsType<CipherCreatePendingRecord>(first.TryUpdate(
                firstReservation.Record,
                terminal));
            Assert.True(first.RemoveConfirmed(upgraded));

            Assert.Throws<IOException>(() => second.TryCreate(pending));
            Assert.Equal(
                CipherCreatePendingReadStatus.Absent,
                second.Read(pending.Witness.AccountId, pending.Witness.Endpoints).Status);

            firstReservation.Dispose();
            using var replacement = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                second.TryCreate(pending));
            Assert.NotEqual(
                firstReservation.Record.ReservationId,
                replacement.Record.ReservationId);
            Assert.Null(second.TryUpdate(upgraded, terminal));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void WriteThroughReplaceFailure_PreservesOldRecordAndCleansTemporaryFile()
    {
        var path = NewPath();
        var store = new DpapiCipherCreatePendingStore(path, new TestProtectionPolicy());
        var pending = Pending('A');
        try
        {
            using var reservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                store.TryCreate(pending));
            var terminal = pending with
            {
                CommitConfirmed = true,
                ExpectedCipherId = "server-id-1",
            };

            using (var blocker = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                Assert.Throws<IOException>(() => store.TryUpdate(reservation.Record, terminal));
            }

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            var recovered = AssertFoundRecord(store.Read(
                pending.Witness.AccountId,
                pending.Witness.Endpoints));
            Assert.Equal(reservation.Record.Revision, recovered.Revision);
            Assert.False(recovered.Pending.CommitConfirmed);
            Assert.Null(recovered.Pending.ExpectedCipherId);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void ProductionSource_UsesWriteThroughAtomicReplacement()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "App",
            "Services",
            "DpapiCipherCreatePendingStore.cs"));

        Assert.Contains("FileOptions.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("stream.Flush(flushToDisk: true)", source, StringComparison.Ordinal);
        Assert.Contains(
            "MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SetLastError = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FakeProtection_ClearsOwnedIntermediateBuffers()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        try
        {
            var store = new DpapiCipherCreatePendingStore(path, protection);
            using var reservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                store.TryCreate(Pending('A')));
            AssertCleared(protection.LastProtectPlaintext);
            AssertCleared(protection.LastProtectEntropy);
            AssertCleared(protection.LastProtectOutput);

            Assert.Equal(
                CipherCreatePendingReadStatus.Found,
                store.Read("account-1", Pending('A').Witness.Endpoints).Status);
            AssertCleared(protection.LastUnprotectProtectedData);
            AssertCleared(protection.LastUnprotectEntropy);
            AssertCleared(protection.LastUnprotectPlaintext);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void BoundedInputAndStrictSchemaFailClosed()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        try
        {
            var oversizedAccount = Pending('A') with
            {
                Witness = new CipherCreateWitness(
                    new string('a', PersistedCipherCreatePendingEntry.MaximumAccountIdLength + 1),
                    Pending('A').Witness.Endpoints,
                    CipherType.Login,
                    null,
                    [],
                    [],
                    new string('A', 64)),
            };
            var store = new DpapiCipherCreatePendingStore(path, protection);
            Assert.Throws<InvalidDataException>(() => store.TryCreate(oversizedAccount));
            var oversizedCollections = Pending('A') with
            {
                Witness = new CipherCreateWitness(
                    "account-1",
                    Pending('A').Witness.Endpoints,
                    CipherType.Login,
                    null,
                    Enumerable.Range(
                        0,
                        PersistedCipherCreatePendingEntry.MaximumCollectionIds + 1)
                        .Select(index => $"collection-{index}")
                        .ToArray(),
                    [],
                    new string('A', 64)),
            };
            Assert.Throws<InvalidDataException>(() => store.TryCreate(oversizedCollections));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var oversizedFile = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                oversizedFile.SetLength(4L * 1024 * 1024 + 1);
            Assert.Equal(
                CipherCreatePendingReadStatus.Corrupt,
                store.Read("account-1", Pending('A').Witness.Endpoints).Status);

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.CipherCreatePendingLedger\",\"schemaVersion\":1,\"entries\":[],\"unexpected\":true}");
            Assert.Equal(
                CipherCreatePendingReadStatus.Corrupt,
                store.Read("account-1", Pending('A').Witness.Endpoints).Status);
            Assert.ThrowsAny<Exception>(() => store.TryCreate(Pending('A')));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void CurrentUserDpapi_RestoresAfterRestartWithoutPersistingPlaintext()
    {
        var path = NewPath();
        var pending = Pending('D');
        try
        {
            var store = new DpapiCipherCreatePendingStore(path);
            using var reservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                store.TryCreate(pending));

            var persisted = File.ReadAllBytes(path);
            var accountBytes = Encoding.UTF8.GetBytes(pending.Witness.AccountId);
            var fingerprintBytes = Encoding.ASCII.GetBytes(pending.Witness.KeyFingerprint);
            try
            {
                Assert.False(ContainsSequence(persisted, accountBytes));
                Assert.False(ContainsSequence(persisted, fingerprintBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fingerprintBytes);
                CryptographicOperations.ZeroMemory(accountBytes);
                CryptographicOperations.ZeroMemory(persisted);
            }

            reservation.Dispose();

            var reopened = new DpapiCipherCreatePendingStore(path);
            var recoveredRecord = AssertFoundRecord(reopened.Read(
                pending.Witness.AccountId,
                pending.Witness.Endpoints));
            var recovered = recoveredRecord.Pending;
            Assert.False(recoveredRecord.IsInFlight);
            Assert.Equal(pending.Witness.KeyFingerprint, recovered.Witness.KeyFingerprint);
            Assert.False(recovered.CommitConfirmed);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void CurrentUserDpapi_WrongPurposeEntropyCannotOpenValidSchema()
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
                PersistedCipherCreatePendingLedger.Empty,
                AppJsonContext.Default.PersistedCipherCreatePendingLedger);
            domain = Encoding.UTF8.GetBytes("WinVaultWarden.SessionIndexProtection.v1");
            entropy = SHA256.HashData(domain);
            protectedBytes = ProtectedData.Protect(
                plaintext,
                entropy,
                DataProtectionScope.CurrentUser);
            framed = new byte[8 + protectedBytes.Length];
            "WVWCPD01"u8.CopyTo(framed);
            protectedBytes.CopyTo(framed, 8);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, framed);

            var store = new DpapiCipherCreatePendingStore(path);
            Assert.Equal(
                CipherCreatePendingReadStatus.Corrupt,
                store.Read("account-1", Pending('A').Witness.Endpoints).Status);
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
    public void TokenAndCacheClear_DoNotRemovePendingCreateLedger()
    {
        var path = NewPath();
        var directory = Path.GetDirectoryName(path)!;
        var pending = Pending('E');
        try
        {
            var store = new DpapiCipherCreatePendingStore(path);
            using var reservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                store.TryCreate(pending));

            new DpapiTokenStore(Path.Combine(directory, "session.bin")).Clear();
            new DpapiVaultCacheStore(Path.Combine(directory, "vault-cache.bin"))
                .ClearVaultCache();

            Assert.True(File.Exists(path));
            Assert.Equal(
                CipherCreatePendingReadStatus.Found,
                new DpapiCipherCreatePendingStore(path).Read(
                    pending.Witness.AccountId,
                    pending.Witness.Endpoints).Status);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void AccountDeletionGuard_BlocksEveryUpsertButAllowsCleanup()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var pending = Pending('A');
        var denied = new DpapiCipherCreatePendingStore(
            path,
            protection,
            beforeWrite: null,
            writeGuard: new DenyingWriteGuard());
        try
        {
            Assert.Throws<AccountDeletionWriteBlockedException>(() =>
                denied.TryCreate(pending));
            Assert.False(File.Exists(path));

            var allowed = new DpapiCipherCreatePendingStore(path, protection);
            using var reservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                allowed.TryCreate(pending));
            Assert.Throws<AccountDeletionWriteBlockedException>(() =>
                denied.TryUpdate(reservation.Record, pending));
            Assert.Throws<AccountDeletionWriteBlockedException>(() =>
                denied.Release(reservation.Record, pending));

            var released = Assert.IsType<CipherCreatePendingRecord>(allowed.Release(
                reservation.Record,
                pending));
            reservation.Dispose();
            Assert.True(denied.Abandon(released));
            Assert.Equal(
                CipherCreatePendingReadStatus.Absent,
                denied.Read(pending.Witness.AccountId, pending.Witness.Endpoints).Status);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void Read_WhenMaintenanceGuardRejectsDoesNotRewriteRecoverableInFlightRecord()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var pending = Pending('B');
        try
        {
            var allowed = new DpapiCipherCreatePendingStore(path, protection);
            var reservation = Assert.IsAssignableFrom<ICipherCreatePendingReservation>(
                allowed.TryCreate(pending));
            reservation.Dispose();

            var guard = new DenyingWriteGuard();
            var denied = new DpapiCipherCreatePendingStore(
                path,
                protection,
                beforeWrite: null,
                writeGuard: guard);
            var readOnly = AssertFoundRecord(denied.Read(
                pending.Witness.AccountId,
                pending.Witness.Endpoints));

            Assert.True(readOnly.IsInFlight);
            Assert.Equal(1, readOnly.Revision);
            Assert.Equal(1, guard.AcquireCount);

            var recovered = AssertFoundRecord(allowed.Read(
                pending.Witness.AccountId,
                pending.Witness.Endpoints));
            Assert.False(recovered.IsInFlight);
            Assert.True(allowed.Abandon(recovered));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    private static CipherCreatePending Pending(
        char fingerprint,
        ServerEndpoints? endpoints = null) => new(
        new CipherCreateWitness(
            "account-1",
            endpoints ?? ServerEndpoints.ForSelfHosted("https://vault.example/base"),
            CipherType.Login,
            null,
            ["collection-1"],
            ["existing-1", "existing-2"],
            new string(fingerprint, 64)),
        CommitConfirmed: false,
        ExpectedCipherId: null);

    private sealed class DenyingWriteGuard : IAccountDeletionLocalWriteGuard
    {
        public int AcquireCount { get; private set; }

        public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints)
        {
            AcquireCount++;
            return null;
        }
    }

    private static ServerEndpoints WithAlternateApi(ServerEndpoints endpoints) => new(
        endpoints.WebVault,
        new ServerEndpoint("https://api.example/custom"),
        endpoints.Identity,
        endpoints.Notifications,
        endpoints.Icons,
        endpoints.SendWeb);

    private static CipherCreatePending AssertFound(CipherCreatePendingReadResult result)
    {
        return AssertFoundRecord(result).Pending;
    }

    private static CipherCreatePendingRecord AssertFoundRecord(
        CipherCreatePendingReadResult result)
    {
        Assert.Equal(CipherCreatePendingReadStatus.Found, result.Status);
        return Assert.IsType<CipherCreatePendingRecord>(result.Record);
    }

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "cipher-create-pending.bin");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinVaultWarden.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("The repository root could not be located.");
    }

    private static void DeleteScope(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void WriteProtectedJson(
        string path,
        TestProtectionPolicy protection,
        string json)
    {
        var plaintext = Encoding.UTF8.GetBytes(json);
        var domain = Encoding.UTF8.GetBytes(
            "WinVaultWarden.CipherCreatePendingLedgerProtection.v1");
        var entropy = SHA256.HashData(domain);
        var protectedBytes = protection.Protect(plaintext, entropy);
        var framed = new byte[8 + protectedBytes.Length];
        try
        {
            "WVWCPD01"u8.CopyTo(framed);
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

    private static void AssertCleared(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        Assert.All(bytes!, value => Assert.Equal(0, value));
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0)
            return true;
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

    private sealed class UnsupportedProtectionPolicy : ICipherCreatePendingProtectionPolicy
    {
        public bool IsSupported => false;

        public byte[] Protect(byte[] plaintext, byte[] entropy) => throw new InvalidOperationException();

        public byte[] Unprotect(byte[] protectedData, byte[] entropy) => throw new InvalidOperationException();
    }

    private sealed class TestProtectionPolicy : ICipherCreatePendingProtectionPolicy
    {
        public bool IsSupported => true;
        public byte[]? LastProtectPlaintext { get; private set; }
        public byte[]? LastProtectEntropy { get; private set; }
        public byte[]? LastProtectOutput { get; private set; }
        public byte[]? LastUnprotectProtectedData { get; private set; }
        public byte[]? LastUnprotectEntropy { get; private set; }
        public byte[]? LastUnprotectPlaintext { get; private set; }

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            LastProtectPlaintext = plaintext;
            LastProtectEntropy = entropy;
            var output = new byte[entropy.Length + plaintext.Length];
            entropy.CopyTo(output, 0);
            for (var index = 0; index < plaintext.Length; index++)
                output[entropy.Length + index] = (byte)(plaintext[index] ^ entropy[index % entropy.Length]);
            LastProtectOutput = output;
            return output;
        }

        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
            LastUnprotectProtectedData = protectedData;
            LastUnprotectEntropy = entropy;
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
            return plaintext;
        }
    }
}
