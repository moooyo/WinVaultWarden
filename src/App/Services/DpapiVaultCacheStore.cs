using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

/// <summary>
/// Stores account-bound vault cache collections with CurrentUser DPAPI protection.
/// </summary>
public sealed class DpapiVaultCacheStore : ICacheStore
{
    private const int MaximumFileLength = 128 * 1024 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private readonly IAccountDeletionLocalWriteGuard _writeGuard;
    private readonly Func<string, byte[]>? _reader;

    public DpapiVaultCacheStore(IAccountDeletionLocalWriteGuard writeGuard)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden", "vault-cache.bin"), writeGuard)
    {
    }

    public DpapiVaultCacheStore(string path)
        : this(path, AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiVaultCacheStore(
        string path,
        IAccountDeletionLocalWriteGuard writeGuard)
        : this(path, writeGuard, reader: null)
    {
    }

    internal DpapiVaultCacheStore(
        string path,
        Func<string, byte[]> reader)
        : this(path, AllowAllAccountDeletionLocalWriteGuard.Instance, reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
    }

    internal DpapiVaultCacheStore(
        string path,
        IAccountDeletionLocalWriteGuard writeGuard,
        Func<string, byte[]>? reader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(writeGuard);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException(
                "Vault cache path must have a parent directory.",
                nameof(path));
        _lockPath = _path + ".lock";
        _writeGuard = writeGuard;
        _reader = reader;
    }

    public void SaveVaultCache(VaultCacheEntry entry)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (!entry.TryMigrate(out var normalized))
            throw new InvalidDataException("Vault cache binding is invalid.");

        using var writeLease = _writeGuard.AcquireOrThrow(
            normalized.AccountId,
            normalized.Endpoints!);
        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var readStatus = ReadCollectionForRemoval(out var collection);
            IReadOnlyList<VaultCacheEntry> existing = readStatus switch
            {
                StrictCollectionReadStatus.Found => collection.Entries,
                StrictCollectionReadStatus.Absent => [],
                StrictCollectionReadStatus.Corrupt => throw new InvalidDataException(
                    "Vault cache collection is corrupt."),
                _ => throw new IOException("Vault cache collection is unavailable."),
            };
            var entries = new[] { normalized }.Concat(existing.Where(candidate =>
                candidate.AccountId != normalized.AccountId
                || candidate.Endpoints != normalized.Endpoints));
            WriteCollection(VaultCacheCollection.FromMostRecent(entries));
        }
    }

    public bool TryLoadVaultCache(out VaultCacheEntry entry)
    {
        lock (_gate)
        {
            entry = null!;
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                if (!TryReadCollection(out var collection))
                    return false;
                entry = collection.Entries[0];
                return true;
            }
            catch (Exception ex) when (IsReadableFailure(ex))
            {
                return false;
            }
        }
    }

    public bool TryLoadVaultCache(
        string accountId,
        ServerEndpoints endpoints,
        out VaultCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);

        lock (_gate)
        {
            entry = null!;
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                if (!TryReadCollection(out var collection))
                    return false;
                entry = collection.Entries.FirstOrDefault(candidate =>
                    candidate.AccountId == accountId && candidate.Endpoints == endpoints)!;
                if (entry is null)
                    return false;
                return true;
            }
            catch (Exception ex) when (IsReadableFailure(ex))
            {
                entry = null!;
                return false;
            }
        }
    }

    public void ClearVaultCache()
    {
        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            if (File.Exists(_path))
                File.Delete(_path);
            DeleteOwnedTemporaryFilesStrict();
        }
    }

    public void ClearVaultCache(string accountId, ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!OperatingSystem.IsWindows())
            return;

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            DeleteOwnedTemporaryFilesStrict();
            if (ReadCollectionForRemoval(out var collection)
                != StrictCollectionReadStatus.Found)
            {
                return;
            }
            var remaining = collection.Entries.Where(candidate =>
                candidate.AccountId != accountId || candidate.Endpoints != endpoints).ToArray();
            if (remaining.Length == collection.Entries.Count)
                return;
            if (remaining.Length == 0)
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            else
            {
                WriteCollection(VaultCacheCollection.FromMostRecent(remaining));
            }
        }
    }

    public VaultCacheRemovalStatus RemoveVaultCacheExact(
        string accountId,
        ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!OperatingSystem.IsWindows())
            return VaultCacheRemovalStatus.Unavailable;

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                if (!TryDeleteOwnedTemporaryFiles(out var removedTemporaryFile))
                    return VaultCacheRemovalStatus.Unavailable;

                var readStatus = ReadCollectionForRemoval(out var collection);
                if (readStatus == StrictCollectionReadStatus.Absent)
                    return removedTemporaryFile
                        ? VaultCacheRemovalStatus.Removed
                        : VaultCacheRemovalStatus.Absent;
                if (readStatus == StrictCollectionReadStatus.Corrupt)
                    return VaultCacheRemovalStatus.Corrupt;
                if (readStatus == StrictCollectionReadStatus.Unavailable)
                    return VaultCacheRemovalStatus.Unavailable;

                var remaining = collection.Entries.Where(candidate =>
                    candidate.AccountId != accountId
                    || candidate.Endpoints != endpoints).ToArray();
                if (remaining.Length == collection.Entries.Count)
                    return removedTemporaryFile
                        ? VaultCacheRemovalStatus.Removed
                        : VaultCacheRemovalStatus.Absent;
                if (remaining.Length == 0)
                    File.Delete(_path);
                else
                    WriteCollection(VaultCacheCollection.FromMostRecent(remaining));

                return VaultCacheRemovalStatus.Removed;
            }
            catch (Exception exception) when (exception is
                CryptographicException or JsonException or InvalidDataException
                    or ArgumentException)
            {
                return VaultCacheRemovalStatus.Corrupt;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return VaultCacheRemovalStatus.Unavailable;
            }
        }
    }

    private StrictCollectionReadStatus ReadCollectionForRemoval(
        out VaultCacheCollection collection)
    {
        collection = null!;
        byte[]? protectedBytes = null;
        byte[]? jsonBytes = null;
        try
        {
            try
            {
                protectedBytes = _reader is null
                    ? ReadAllBytesShared(_path)
                    : _reader(_path);
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or DirectoryNotFoundException)
            {
                return StrictCollectionReadStatus.Absent;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException)
            {
                return StrictCollectionReadStatus.Unavailable;
            }

            jsonBytes = ProtectedData.Unprotect(
                protectedBytes,
                null,
                DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(jsonBytes);
            var root = document.RootElement;
            var isCollection = root.TryGetProperty("entries", out _)
                || root.TryGetProperty("Entries", out _);
            if (!isCollection)
            {
                var legacy = JsonSerializer.Deserialize(
                    jsonBytes,
                    AppJsonContext.Default.VaultCacheEntry);
                if (legacy is null || !legacy.TryMigrate(out var normalizedLegacy))
                    return StrictCollectionReadStatus.Corrupt;
                collection = VaultCacheCollection.FromMostRecent((VaultCacheEntry[])[normalizedLegacy]);
                return StrictCollectionReadStatus.Found;
            }

            var loaded = JsonSerializer.Deserialize(
                jsonBytes,
                AppJsonContext.Default.VaultCacheCollection);
            if (loaded is null
                || loaded.SchemaVersion != VaultCacheCollection.CurrentSchemaVersion
                || loaded.Entries is null
                || loaded.Entries.Count is 0
                    or > VaultCacheCollection.MaximumEntries)
            {
                return StrictCollectionReadStatus.Corrupt;
            }

            var entries = new List<VaultCacheEntry>(loaded.Entries.Count);
            var seen = new HashSet<(string AccountId, ServerEndpoints Endpoints)>();
            foreach (var candidate in loaded.Entries)
            {
                if (candidate is null
                    || !candidate.TryMigrate(out var normalized)
                    || !seen.Add((normalized.AccountId, normalized.Endpoints!)))
                {
                    return StrictCollectionReadStatus.Corrupt;
                }
                entries.Add(normalized);
            }

            collection = new VaultCacheCollection(
                VaultCacheCollection.CurrentSchemaVersion,
                entries);
            return StrictCollectionReadStatus.Found;
        }
        catch (Exception exception) when (exception is
            CryptographicException or JsonException or InvalidDataException
                or FormatException or ArgumentException)
        {
            return StrictCollectionReadStatus.Corrupt;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return StrictCollectionReadStatus.Unavailable;
        }
        finally
        {
            if (jsonBytes is not null)
                CryptographicOperations.ZeroMemory(jsonBytes);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private bool TryReadCollection(out VaultCacheCollection collection)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI cache storage is only supported on Windows.");
        collection = null!;
        if (!File.Exists(_path))
            return false;

        byte[]? protectedBytes = null;
        byte[]? jsonBytes = null;
        try
        {
            protectedBytes = _reader is null
                ? ReadAllBytesShared(_path)
                : _reader(_path);
            jsonBytes = ProtectedData.Unprotect(
                protectedBytes,
                null,
                DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(jsonBytes);
            var root = document.RootElement;
            var isCollection = root.TryGetProperty("entries", out _)
                || root.TryGetProperty("Entries", out _);
            if (isCollection)
            {
                var loaded = JsonSerializer.Deserialize(
                    jsonBytes,
                    AppJsonContext.Default.VaultCacheCollection);
                if (loaded is null || !loaded.TryNormalize(out collection))
                    return false;
                return true;
            }

            var legacy = JsonSerializer.Deserialize(
                jsonBytes,
                AppJsonContext.Default.VaultCacheEntry);
            if (legacy is null || !legacy.TryMigrate(out var normalized))
                return false;
            collection = VaultCacheCollection.FromMostRecent((VaultCacheEntry[])[normalized]);
            return true;
        }
        finally
        {
            if (jsonBytes is not null)
                CryptographicOperations.ZeroMemory(jsonBytes);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private void WriteCollection(VaultCacheCollection collection)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI cache storage is only supported on Windows.");
        if (!collection.TryNormalize(out var normalized))
            throw new InvalidDataException("Vault cache collection is invalid.");

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            normalized,
            AppJsonContext.Default.VaultCacheCollection);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                jsonBytes,
                null,
                DataProtectionScope.CurrentUser);
            if (protectedBytes.Length is <= 0 or > MaximumFileLength)
                throw new InvalidDataException("Protected vault cache length is invalid.");
            Directory.CreateDirectory(_directoryPath);
            var temporaryPath = Path.Combine(
                _directoryPath,
                $".{Path.GetFileName(_path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(protectedBytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, _path, overwrite: true);
                temporaryPath = string.Empty;
            }
            finally
            {
                if (temporaryPath.Length > 0)
                    TryDeleteTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private FileStream AcquireStoreLock()
    {
        Directory.CreateDirectory(_directoryPath);
        var stopwatch = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (attempt < StoreLockAttempts)
            {
                Thread.Sleep(StoreLockRetryMilliseconds);
            }

            if (stopwatch.ElapsedMilliseconds
                >= StoreLockAttempts * StoreLockRetryMilliseconds)
            {
                throw new IOException("Timed out acquiring the vault-cache store lock.");
            }
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumFileLength)
            throw new InvalidDataException("Vault cache file length is invalid.");

        var bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
        try
        {
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private void DeleteOwnedTemporaryFilesStrict()
    {
        if (!TryDeleteOwnedTemporaryFiles(out _))
            throw new IOException("One or more vault-cache temporary files could not be deleted.");
    }

    private bool TryDeleteOwnedTemporaryFiles(out bool removedAny)
    {
        removedAny = false;
        IReadOnlyList<string> paths;
        try
        {
            paths = EnumerateOwnedTemporaryFiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        foreach (var path in paths)
        {
            if (!TryDeleteTemporaryFile(path))
                return false;
            removedAny = true;
        }
        return true;
    }

    private IReadOnlyList<string> EnumerateOwnedTemporaryFiles()
    {
        var paths = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_directoryPath, "*.tmp"))
            {
                if (IsOwnedTemporaryFileName(Path.GetFileName(path)))
                    paths.Add(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        return paths;
    }

    private bool IsOwnedTemporaryFileName(string fileName)
    {
        var storeFileName = Path.GetFileName(_path);
        if (string.Equals(fileName, storeFileName + ".tmp", StringComparison.Ordinal))
            return true;

        var prefix = "." + storeFileName + ".";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - 4);
        var separator = suffix.IndexOf('.');
        return separator > 0
            && suffix[(separator + 1)..].IndexOf('.') < 0
            && IsCanonicalProcessId(suffix[..separator])
            && Guid.TryParseExact(suffix[(separator + 1)..], "N", out var temporaryId)
            && temporaryId != Guid.Empty;
    }

    private static bool IsCanonicalProcessId(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value[0] == '0')
            return false;
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }
        return int.TryParse(value, out var processId) && processId > 0;
    }

    private static bool TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReadableFailure(Exception exception) => exception is
        PlatformNotSupportedException
        or CryptographicException
        or JsonException
        or IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or ArgumentException;

    private enum StrictCollectionReadStatus
    {
        Found,
        Absent,
        Corrupt,
        Unavailable,
    }

}
