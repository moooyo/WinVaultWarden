using App.Services;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public class DpapiVaultCacheStoreTests
{
    [Fact]
    public void SaveLoadClear_RoundTrips_AndProtectsPayload()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "vault-cache.bin");
        var store = new DpapiVaultCacheStore(path);
        var entry = new VaultCacheEntry(
            2,
            "account-1",
            new ServerOrigin("https://vault.example"),
            7,
            "me@example.com",
            DateTimeOffset.UnixEpoch,
            "{\"secret-cipher-json\":\"2.abc\"}");

        store.SaveVaultCache(entry);

        // DPAPI protection must keep the plaintext payload out of the file.
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("secret-cipher-json", raw);
        Assert.False(File.Exists(path + ".tmp"));

        Assert.True(store.TryLoadVaultCache(out var loaded));
        Assert.True(entry.TryMigrate(out var migrated));
        Assert.Equal(migrated, loaded);

        store.ClearVaultCache();
        Assert.False(store.TryLoadVaultCache(out _));
    }

    [WindowsFact]
    public void MultipleAccounts_LoadAndClearOnlyTheRequestedCache()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"),
            "vault-cache.bin");
        var first = Entry("account-a", "https://a.example", "payload-a");
        var second = Entry("account-b", "https://b.example", "payload-b");
        var store = new DpapiVaultCacheStore(path);

        store.SaveVaultCache(first);
        store.SaveVaultCache(second);

        Assert.True(store.TryLoadVaultCache(first.AccountId, first.Endpoints!, out var loaded));
        Assert.Equal("payload-a", loaded.SyncResponseJson);
        Assert.Equal(
            VaultCacheRemovalStatus.Removed,
            store.RemoveVaultCacheExact(first.AccountId, first.Endpoints!));
        Assert.Equal(
            VaultCacheRemovalStatus.Absent,
            store.RemoveVaultCacheExact(first.AccountId, first.Endpoints!));
        Assert.False(store.TryLoadVaultCache(first.AccountId, first.Endpoints!, out _));
        Assert.True(store.TryLoadVaultCache(second.AccountId, second.Endpoints!, out loaded));
        Assert.Equal("payload-b", loaded.SyncResponseJson);

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("payload-a", raw);
        Assert.DoesNotContain("payload-b", raw);
    }

    [Fact]
    public void TryLoad_WhenReadThrowsIOException_ReturnsFalse()
    {
        AssertReadFailureReturnsFalse(new IOException("read failed"));
    }

    [Fact]
    public void TryLoad_WhenReadThrowsUnauthorizedAccess_ReturnsFalse()
    {
        AssertReadFailureReturnsFalse(
            new UnauthorizedAccessException("read denied"));
    }

    [WindowsFact]
    public void Save_RejectingDeletionGuardThrowsBeforeCreatingStoreArtifacts()
    {
        var path = NewPath();
        var entry = Entry("account-a", "https://a.example", "payload-a");
        var guard = new RecordingWriteGuard(allow: false);
        try
        {
            var store = new DpapiVaultCacheStore(path, guard);

            Assert.Throws<AccountDeletionWriteBlockedException>(
                () => store.SaveVaultCache(entry));

            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(entry.AccountId, guard.LastAccountId);
            Assert.Equal(entry.Endpoints, guard.LastEndpoints);
            Assert.Equal(0, guard.ActiveLeaseCount);
            Assert.Equal(0, guard.DisposedLeaseCount);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(path + ".lock"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void Save_HoldsDeletionLeaseAcrossReadModifyWriteAndReleasesIt()
    {
        var path = NewPath();
        var original = Entry("account-a", "https://a.example", "payload-a");
        var replacement = original with { SyncResponseJson = "payload-new" };
        var committedWhenLeaseDisposed = false;
        var guard = new RecordingWriteGuard(
            allow: true,
            onLeaseDisposed: () =>
            {
                committedWhenLeaseDisposed = new DpapiVaultCacheStore(path)
                    .TryLoadVaultCache(
                        replacement.AccountId,
                        replacement.Endpoints!,
                        out var committed)
                    && committed.SyncResponseJson == replacement.SyncResponseJson;
            });
        var observedActiveLease = false;
        try
        {
            new DpapiVaultCacheStore(path).SaveVaultCache(original);
            var store = new DpapiVaultCacheStore(
                path,
                guard,
                currentPath =>
                {
                    observedActiveLease = guard.ActiveLeaseCount == 1;
                    return File.ReadAllBytes(currentPath);
                });

            store.SaveVaultCache(replacement);

            Assert.True(observedActiveLease);
            Assert.True(committedWhenLeaseDisposed);
            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(0, guard.ActiveLeaseCount);
            Assert.Equal(1, guard.DisposedLeaseCount);
            Assert.True(store.TryLoadVaultCache(
                replacement.AccountId,
                replacement.Endpoints!,
                out var loaded));
            Assert.Equal(replacement.SyncResponseJson, loaded.SyncResponseJson);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void Save_WhenStoreMutationFailsReleasesDeletionLease()
    {
        var path = NewPath();
        var entry = Entry("account-a", "https://a.example", "payload-a");
        var guard = new RecordingWriteGuard(allow: true);
        try
        {
            new DpapiVaultCacheStore(path).SaveVaultCache(entry);
            var store = new DpapiVaultCacheStore(
                path,
                guard,
                _ => throw new IOException("read failed"));

            Assert.Throws<IOException>(() => store.SaveVaultCache(entry));

            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(0, guard.ActiveLeaseCount);
            Assert.Equal(1, guard.DisposedLeaseCount);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void RemovalAndClearOperationsDoNotConsultRejectingDeletionGuard()
    {
        var path = NewPath();
        var target = Entry("account-a", "https://a.example", "payload-a");
        var other = Entry("account-b", "https://b.example", "payload-b");
        var seeder = new DpapiVaultCacheStore(path);
        var guard = new RecordingWriteGuard(allow: false);
        var guarded = new DpapiVaultCacheStore(path, guard);
        try
        {
            seeder.SaveVaultCache(target);
            seeder.SaveVaultCache(other);

            Assert.Equal(
                VaultCacheRemovalStatus.Removed,
                guarded.RemoveVaultCacheExact(target.AccountId, target.Endpoints!));
            guarded.ClearVaultCache(other.AccountId, other.Endpoints!);
            seeder.SaveVaultCache(target);
            guarded.ClearVaultCache();

            Assert.Equal(0, guard.AcquireCount);
            Assert.False(guarded.TryLoadVaultCache(out _));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void TryLoadLegacyEntryDoesNotWriteMigrationOrCreateStoreArtifacts()
    {
        var path = NewPath();
        var entry = Entry("account-a", "https://a.example", "payload-a");
        try
        {
            WriteProtectedEntry(path, entry);
            var before = File.ReadAllBytes(path);

            Assert.True(new DpapiVaultCacheStore(path).TryLoadVaultCache(out var loaded));

            Assert.True(entry.TryMigrate(out var normalized));
            Assert.Equal(normalized, loaded);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.False(File.Exists(path + ".lock"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void MalformedEndpointCollectionFailsClosedWithoutOverwriting()
    {
        var path = NewPath();
        var target = Entry("account-a", "https://a.example", "payload-a");
        var replacement = Entry("account-b", "https://b.example", "payload-b");
        try
        {
            var validJson = JsonSerializer.Serialize(
                new VaultCacheCollection(
                    VaultCacheCollection.CurrentSchemaVersion,
                    [target]),
                AppJsonContext.Default.VaultCacheCollection);
            var malformedJson = validJson.Replace(
                "https://a.example",
                "ftp://a.example",
                StringComparison.Ordinal);
            Assert.NotEqual(validJson, malformedJson);
            WriteProtectedJson(path, malformedJson);
            var before = File.ReadAllBytes(path);
            var store = new DpapiVaultCacheStore(path);

            Assert.False(store.TryLoadVaultCache(out _));
            Assert.Equal(
                VaultCacheRemovalStatus.Corrupt,
                store.RemoveVaultCacheExact(target.AccountId, target.Endpoints!));
            Assert.Throws<InvalidDataException>(
                () => store.SaveVaultCache(replacement));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void RemoveExact_OnlyTemporaryFileExists_RemovesCrashResidue()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", [1, 2, 3]);
        var target = Entry("account-a", "https://a.example", "payload-a");

        var result = new DpapiVaultCacheStore(path).RemoveVaultCacheExact(
            target.AccountId,
            target.Endpoints!);

        Assert.Equal(VaultCacheRemovalStatus.Removed, result);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [WindowsFact]
    public void RemoveExact_DeletesOnlyStrictlyOwnedUniqueTemporaryFiles()
    {
        var path = NewPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var owned = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.123.{Guid.NewGuid():N}.tmp");
        var malformed = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.0.{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(owned, [1, 2, 3]);
        File.WriteAllBytes(malformed, [4, 5, 6]);
        var target = Entry("account-a", "https://a.example", "payload-a");

        var result = new DpapiVaultCacheStore(path).RemoveVaultCacheExact(
            target.AccountId,
            target.Endpoints!);

        Assert.Equal(VaultCacheRemovalStatus.Removed, result);
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(malformed));
    }

    [Fact]
    public void ProductionSource_UsesBoundedWriteThroughUniqueTemporaryFiles()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "App",
            "Services",
            "DpapiVaultCacheStore.cs"));

        Assert.Contains("MaximumFileLength", source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("stream.Flush(flushToDisk: true)", source, StringComparison.Ordinal);
        Assert.Contains("Guid.NewGuid():N", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, _path, overwrite: true)", source, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void RemoveExact_MainAndStaleTemporaryFile_RemovesTargetAndPreservesOtherAccount()
    {
        var path = NewPath();
        var target = Entry("account-a", "https://a.example", "payload-a");
        var other = Entry("account-b", "https://b.example", "payload-b");
        var store = new DpapiVaultCacheStore(path);
        store.SaveVaultCache(target);
        store.SaveVaultCache(other);
        File.WriteAllBytes(path + ".tmp", [9, 8, 7]);

        Assert.Equal(
            VaultCacheRemovalStatus.Removed,
            store.RemoveVaultCacheExact(target.AccountId, target.Endpoints!));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(store.TryLoadVaultCache(target.AccountId, target.Endpoints!, out _));
        Assert.True(store.TryLoadVaultCache(other.AccountId, other.Endpoints!, out var loaded));
        Assert.Equal(other.SyncResponseJson, loaded.SyncResponseJson);
    }

    [WindowsFact]
    public void RemoveExact_LockedTemporaryFile_ReturnsUnavailableAndPreservesMain()
    {
        var path = NewPath();
        var target = Entry("account-a", "https://a.example", "payload-a");
        var store = new DpapiVaultCacheStore(path);
        store.SaveVaultCache(target);
        File.WriteAllBytes(path + ".tmp", [9]);
        using var locked = new FileStream(
            path + ".tmp",
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Equal(
            VaultCacheRemovalStatus.Unavailable,
            store.RemoveVaultCacheExact(target.AccountId, target.Endpoints!));
        Assert.True(File.Exists(path));
    }

    [WindowsFact]
    public void RemoveExact_InvalidTargetEntryAlongsideValidAccount_ReturnsCorruptWithoutRewrite()
    {
        var path = NewPath();
        var target = Entry("account-a", "https://a.example", "payload-a") with
        {
            Email = string.Empty,
        };
        var other = Entry("account-b", "https://b.example", "payload-b");
        WriteProtectedCollection(
            path,
            new VaultCacheCollection(
                VaultCacheCollection.CurrentSchemaVersion,
                [target, other]));
        var before = File.ReadAllBytes(path);

        var result = new DpapiVaultCacheStore(path).RemoveVaultCacheExact(
            target.AccountId,
            target.Endpoints!);

        Assert.Equal(VaultCacheRemovalStatus.Corrupt, result);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [WindowsFact]
    public void RemoveExact_ReadAccessFailure_ReturnsUnavailableNotAbsent()
    {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        var target = Entry("account-a", "https://a.example", "payload-a");
        var store = new DpapiVaultCacheStore(
            path,
            _ => throw new UnauthorizedAccessException("read denied"));

        Assert.Equal(
            VaultCacheRemovalStatus.Unavailable,
            store.RemoveVaultCacheExact(target.AccountId, target.Endpoints!));
    }

    [WindowsFact]
    public async Task ConcurrentExactRemovalsAcrossInstancesDoNotReviveEitherBinding()
    {
        var path = NewPath();
        var firstEntry = Entry("account-a", "https://a.example", "payload-a");
        var secondEntry = Entry("account-b", "https://b.example", "payload-b");
        using var firstReaderEntered = new ManualResetEventSlim(initialState: false);
        using var releaseFirstReader = new ManualResetEventSlim(initialState: false);
        using var secondRemovalStarted = new ManualResetEventSlim(initialState: false);
        using var secondReaderEntered = new ManualResetEventSlim(initialState: false);
        try
        {
            var seeder = new DpapiVaultCacheStore(path);
            seeder.SaveVaultCache(firstEntry);
            seeder.SaveVaultCache(secondEntry);
            var firstStore = new DpapiVaultCacheStore(
                path,
                currentPath =>
                {
                    var bytes = File.ReadAllBytes(currentPath);
                    firstReaderEntered.Set();
                    releaseFirstReader.Wait(TimeSpan.FromSeconds(5));
                    return bytes;
                });
            var secondStore = new DpapiVaultCacheStore(
                path,
                currentPath =>
                {
                    secondReaderEntered.Set();
                    return File.ReadAllBytes(currentPath);
                });

            var firstRemoval = Task.Factory.StartNew(
                () => firstStore.RemoveVaultCacheExact(
                    firstEntry.AccountId,
                    firstEntry.Endpoints!),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(firstReaderEntered.Wait(TimeSpan.FromSeconds(5)));
            var secondRemoval = Task.Factory.StartNew(
                () =>
                {
                    secondRemovalStarted.Set();
                    return secondStore.RemoveVaultCacheExact(
                        secondEntry.AccountId,
                        secondEntry.Endpoints!);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(secondRemovalStarted.Wait(TimeSpan.FromSeconds(5)));
            var secondReadBeforeFirstCommit = secondReaderEntered.Wait(
                TimeSpan.FromMilliseconds(250));

            releaseFirstReader.Set();
            var results = await Task.WhenAll(firstRemoval, secondRemoval)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(secondReadBeforeFirstCommit);
            Assert.True(secondReaderEntered.IsSet);
            Assert.All(results, result =>
                Assert.Equal(VaultCacheRemovalStatus.Removed, result));
            Assert.False(seeder.TryLoadVaultCache(
                firstEntry.AccountId,
                firstEntry.Endpoints!,
                out _));
            Assert.False(seeder.TryLoadVaultCache(
                secondEntry.AccountId,
                secondEntry.Endpoints!,
                out _));
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.True(File.Exists(path + ".lock"));
        }
        finally
        {
            releaseFirstReader.Set();
            DeleteScope(path);
        }
    }

    private static void AssertReadFailureReturnsFalse(Exception readFailure)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "vault-cache.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, [0]);
            Func<string, byte[]> reader = _ => throw readFailure;
            var store = new DpapiVaultCacheStore(path, reader);

            Assert.False(store.TryLoadVaultCache(out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static VaultCacheEntry Entry(
        string accountId,
        string serverUrl,
        string payload) => new(
        VaultCacheEntry.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        DateTimeOffset.UnixEpoch,
        payload);

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "vault-cache.bin");

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

    private static void WriteProtectedEntry(string path, VaultCacheEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            entry,
            AppJsonContext.Default.VaultCacheEntry);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                json,
                null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static void WriteProtectedJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                jsonBytes,
                null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static void WriteProtectedCollection(
        string path,
        VaultCacheCollection collection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(collection);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                json,
                null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private sealed class RecordingWriteGuard(
        bool allow,
        Action? onLeaseDisposed = null)
        : IAccountDeletionLocalWriteGuard
    {
        private int _acquireCount;
        private int _activeLeaseCount;
        private int _disposedLeaseCount;

        public int AcquireCount => Volatile.Read(ref _acquireCount);

        public int ActiveLeaseCount => Volatile.Read(ref _activeLeaseCount);

        public int DisposedLeaseCount => Volatile.Read(ref _disposedLeaseCount);

        public string? LastAccountId { get; private set; }

        public ServerEndpoints? LastEndpoints { get; private set; }

        public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints)
        {
            Interlocked.Increment(ref _acquireCount);
            LastAccountId = accountId;
            LastEndpoints = endpoints;
            if (!allow)
                return null;

            Interlocked.Increment(ref _activeLeaseCount);
            return new CallbackLease(() =>
            {
                Interlocked.Decrement(ref _activeLeaseCount);
                Interlocked.Increment(ref _disposedLeaseCount);
                onLeaseDisposed?.Invoke();
            });
        }
    }

    private sealed class CallbackLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
