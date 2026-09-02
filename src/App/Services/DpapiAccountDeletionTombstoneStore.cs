using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

public sealed class DpapiAccountDeletionTombstoneStore
    : IAccountDeletionTombstoneStore
{
    private const int MaximumFileLength = 1024 * 1024;
    private const int MaximumPlaintextLength = 768 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;
    private const string EntropyDomain =
        "WinVaultWarden.AccountDeletionTombstoneLedgerProtection.v1";

    private static readonly string[] LedgerPropertyNames =
        ["format", "schemaVersion", "entries"];
    private static readonly string[] EntryPropertyNames =
    [
        "entryId",
        "revision",
        "createdAt",
        "updatedAt",
        "stage",
        "accountId",
        "normalizedEmail",
        "webVault",
        "api",
        "identity",
        "notifications",
        "icons",
        "sendWeb",
        "deviceIdentifier",
    ];

    private static ReadOnlySpan<byte> FileMagic => "WVWADT01"u8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private readonly IAccountDeletionTombstoneProtectionPolicy _protection;
    private readonly IAccountDeletionTombstoneAtomicReplacePolicy _atomicReplace;

    public DpapiAccountDeletionTombstoneStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "account-deletion-tombstones.bin"))
    {
    }

    public DpapiAccountDeletionTombstoneStore(string path)
        : this(
            path,
            CurrentUserDpapiAccountDeletionTombstoneProtection.Instance,
            WindowsWriteThroughAccountDeletionTombstoneAtomicReplacePolicy.Instance)
    {
    }

    internal DpapiAccountDeletionTombstoneStore(
        string path,
        IAccountDeletionTombstoneProtectionPolicy protection,
        IAccountDeletionTombstoneAtomicReplacePolicy atomicReplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(atomicReplace);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException(
                "The account-deletion tombstone path must have a parent directory.",
                nameof(path));
        _lockPath = _path + ".lock";
        _protection = protection;
        _atomicReplace = atomicReplace;
    }

    public AccountDeletionTombstoneReadResult Read(AccountDeletionBinding binding)
    {
        ValidateBinding(binding);
        if (!IsSupported)
            return AccountDeletionTombstoneReadResult.Unavailable;

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                var ledger = ReadLedgerForMutation();
                var entry = ledger.Entries.FirstOrDefault(candidate =>
                    SameBinding(candidate, binding));
                return entry is null
                    ? AccountDeletionTombstoneReadResult.Absent
                    : AccountDeletionTombstoneReadResult.Found(entry.ToRecord());
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return AccountDeletionTombstoneReadResult.Corrupt;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return AccountDeletionTombstoneReadResult.Unavailable;
            }
        }
    }

    public AccountDeletionTombstoneReadAllResult ReadAll()
    {
        if (!IsSupported)
            return AccountDeletionTombstoneReadAllResult.Unavailable;

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                var records = ReadLedgerForMutation().ToRecords();
                return records.Count == 0
                    ? AccountDeletionTombstoneReadAllResult.Absent
                    : AccountDeletionTombstoneReadAllResult.Found(records);
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return AccountDeletionTombstoneReadAllResult.Corrupt;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return AccountDeletionTombstoneReadAllResult.Unavailable;
            }
        }
    }

    private bool IsSupported => _protection.IsSupported && _atomicReplace.IsSupported;

    public AccountDeletionTombstoneRecord? TryCreate(
        AccountDeletionBinding binding,
        DateTimeOffset createdAt)
    {
        ValidateBinding(binding);
        var record = new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            revision: 1,
            createdAt,
            createdAt,
            binding,
            AccountDeletionStage.Prepared);
        var normalized = Normalize(record);
        RequireSupported();

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            if (ledger.Entries.Any(candidate => SameDeletionScope(candidate, binding)))
                return null;
            if (ledger.Entries.Count >= PersistedAccountDeletionTombstoneLedger.MaximumEntries)
            {
                throw new InvalidDataException(
                    "The account-deletion tombstone ledger is full.");
            }

            WriteLedger(PersistedAccountDeletionTombstoneLedger.Create(
                ledger.ToRecords().Append(normalized)));
            TryDeleteTemporaryFiles();
            return normalized;
        }
    }

    public AccountDeletionTombstoneRecord? TryAdvance(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionStage nextStage,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Enum.IsDefined(nextStage))
            throw new ArgumentOutOfRangeException(nameof(nextStage));
        var validTransition =
            expected.Stage == AccountDeletionStage.Prepared
                && nextStage == AccountDeletionStage.ServerConfirmed
            || expected.Stage == AccountDeletionStage.Prepared
                && nextStage == AccountDeletionStage.UncertainLocalCleanupComplete
            || expected.Stage == AccountDeletionStage.ServerConfirmed
                && nextStage == AccountDeletionStage.LocalCleanupComplete;
        if (!validTransition)
        {
            throw new ArgumentException(
                "An account-deletion tombstone must advance through the next durable stage.",
                nameof(nextStage));
        }

        var normalizedExpected = Normalize(expected);
        var normalizedUpdatedAt = updatedAt.ToUniversalTime();
        if (normalizedUpdatedAt < normalizedExpected.UpdatedAt)
        {
            throw new ArgumentException(
                "The update timestamp cannot move backwards.",
                nameof(updatedAt));
        }
        RequireSupported();

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            var records = ledger.ToRecords().ToArray();
            var index = FindIndex(records, normalizedExpected);
            if (index < 0)
                return null;

            var current = records[index];
            var updated = new AccountDeletionTombstoneRecord(
                current.EntryId,
                checked(current.Revision + 1),
                current.CreatedAt,
                normalizedUpdatedAt,
                current.Binding,
                nextStage);
            records[index] = updated;
            WriteLedger(PersistedAccountDeletionTombstoneLedger.Create(records));
            TryDeleteTemporaryFiles();
            return updated;
        }
    }

    public bool Remove(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionTombstoneRemovalReason reason)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));

        var normalizedExpected = Normalize(expected);
        if (!IsRemovalAllowed(normalizedExpected.Stage, reason))
            return false;
        RequireSupported();

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            var records = ledger.ToRecords().ToArray();
            var index = FindIndex(records, normalizedExpected);
            if (index < 0)
                return false;

            WriteLedger(PersistedAccountDeletionTombstoneLedger.Create(
                records.Where((_, candidateIndex) => candidateIndex != index)));
            TryDeleteTemporaryFiles();
            return true;
        }
    }

    private PersistedAccountDeletionTombstoneLedger ReadLedgerForMutation()
    {
        byte[]? fileBytes = null;
        byte[]? protectedBytes = null;
        byte[]? entropy = null;
        byte[]? plaintext = null;
        try
        {
            try
            {
                fileBytes = ReadBounded(_path, MaximumFileLength);
            }
            catch (FileNotFoundException)
            {
                return PersistedAccountDeletionTombstoneLedger.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return PersistedAccountDeletionTombstoneLedger.Empty;
            }

            if (fileBytes.Length <= FileMagic.Length
                || !fileBytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
            {
                throw new InvalidDataException(
                    "The account-deletion tombstone ledger header is invalid.");
            }

            protectedBytes = fileBytes.AsSpan(FileMagic.Length).ToArray();
            entropy = CreateProtectionEntropy();
            plaintext = UnprotectOwned(protectedBytes, entropy);
            ValidateJsonShape(plaintext);
            var loaded = JsonSerializer.Deserialize(
                plaintext,
                AppJsonContext.Default.PersistedAccountDeletionTombstoneLedger);
            if (loaded is null || !loaded.TryNormalize(out var normalized))
            {
                throw new InvalidDataException(
                    "The account-deletion tombstone ledger schema is invalid.");
            }
            return normalized;
        }
        finally
        {
            Clear(plaintext);
            Clear(entropy);
            Clear(protectedBytes);
            Clear(fileBytes);
        }
    }

    private static void ValidateJsonShape(byte[] plaintext)
    {
        using var document = JsonDocument.Parse(
            plaintext,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
        var root = document.RootElement;
        RequireExactProperties(root, LedgerPropertyNames);
        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array
            || entries.GetArrayLength()
                > PersistedAccountDeletionTombstoneLedger.MaximumEntries)
        {
            throw new InvalidDataException(
                "The account-deletion tombstone entry collection is invalid.");
        }

        foreach (var entry in entries.EnumerateArray())
            RequireExactProperties(entry, EntryPropertyNames);
    }

    private static void RequireExactProperties(
        JsonElement element,
        IReadOnlyCollection<string> expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A tombstone JSON object was expected.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedNames.Contains(property.Name)
                || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    "The account-deletion tombstone JSON schema is not canonical.");
            }
        }
        if (seen.Count != expectedNames.Count)
        {
            throw new InvalidDataException(
                "The account-deletion tombstone JSON schema is incomplete.");
        }
    }

    private void WriteLedger(PersistedAccountDeletionTombstoneLedger ledger)
    {
        if (!ledger.TryNormalize(out var normalized))
        {
            throw new InvalidDataException(
                "The account-deletion tombstone ledger is invalid.");
        }

        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                normalized,
                AppJsonContext.Default.PersistedAccountDeletionTombstoneLedger);
            EnsureLength(
                plaintext,
                MaximumPlaintextLength,
                "Account-deletion tombstone ledger plaintext");
            entropy = CreateProtectionEntropy();
            protectedBytes = ProtectOwned(plaintext, entropy);
            EnsureLength(
                protectedBytes,
                MaximumFileLength - FileMagic.Length,
                "Protected account-deletion tombstone ledger");
            framedBytes = Frame(protectedBytes);
            WriteAtomic(framedBytes);
        }
        finally
        {
            Clear(framedBytes);
            Clear(protectedBytes);
            Clear(entropy);
            Clear(plaintext);
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
                throw new IOException(
                    "Timed out acquiring the account-deletion tombstone store lock.");
            }
        }
    }

    private static byte[] ReadBounded(string path, int maximumLength)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumLength)
        {
            throw new InvalidDataException(
                "The account-deletion tombstone ledger file length is invalid.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException(
                    "The account-deletion tombstone ledger changed while being read.");
            }
            return bytes;
        }
        catch
        {
            Clear(bytes);
            throw;
        }
    }

    private byte[] ProtectOwned(byte[] plaintext, byte[] entropy)
    {
        var protectedBytes = _protection.Protect(plaintext, entropy);
        if (protectedBytes is null)
        {
            throw new CryptographicException(
                "The account-deletion tombstone protector returned no payload.");
        }
        if (ReferenceEquals(protectedBytes, plaintext)
            || ReferenceEquals(protectedBytes, entropy))
        {
            throw new CryptographicException(
                "The account-deletion tombstone protector returned an aliased payload.");
        }
        return protectedBytes;
    }

    private byte[] UnprotectOwned(byte[] protectedBytes, byte[] entropy)
    {
        var plaintext = _protection.Unprotect(protectedBytes, entropy);
        if (plaintext is null)
        {
            throw new CryptographicException(
                "The account-deletion tombstone protector returned no plaintext.");
        }

        var owned = ReferenceEquals(plaintext, protectedBytes)
            || ReferenceEquals(plaintext, entropy)
                ? plaintext.ToArray()
                : plaintext;
        try
        {
            EnsureLength(
                owned,
                MaximumPlaintextLength,
                "Account-deletion tombstone ledger plaintext");
            return owned;
        }
        catch
        {
            Clear(owned);
            throw;
        }
    }

    private static byte[] CreateProtectionEntropy()
    {
        var domain = Encoding.UTF8.GetBytes(EntropyDomain);
        try
        {
            return SHA256.HashData(domain);
        }
        finally
        {
            Clear(domain);
        }
    }

    private static byte[] Frame(byte[] protectedBytes)
    {
        var framed = GC.AllocateUninitializedArray<byte>(
            FileMagic.Length + protectedBytes.Length);
        FileMagic.CopyTo(framed);
        protectedBytes.CopyTo(framed, FileMagic.Length);
        return framed;
    }

    private void WriteAtomic(byte[] bytes)
    {
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
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            _atomicReplace.Replace(temporaryPath, _path);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
                TryDeleteFile(temporaryPath);
        }
    }

    private void TryDeleteTemporaryFiles()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                _directoryPath,
                $".{Path.GetFileName(_path)}.*.tmp"))
            {
                TryDeleteFile(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static AccountDeletionTombstoneRecord Normalize(
        AccountDeletionTombstoneRecord record) =>
        PersistedAccountDeletionTombstoneEntry.From(record).ToRecord();

    private static int FindIndex(
        IReadOnlyList<AccountDeletionTombstoneRecord> records,
        AccountDeletionTombstoneRecord expected)
    {
        for (var index = 0; index < records.Count; index++)
        {
            if (RecordsMatch(records[index], expected))
                return index;
        }
        return -1;
    }

    private static bool RecordsMatch(
        AccountDeletionTombstoneRecord current,
        AccountDeletionTombstoneRecord expected) =>
        current.EntryId == expected.EntryId
        && current.Revision == expected.Revision
        && current.CreatedAt == expected.CreatedAt
        && current.UpdatedAt == expected.UpdatedAt
        && current.Stage == expected.Stage
        && BindingsMatch(current.Binding, expected.Binding);

    private static bool SameBinding(
        PersistedAccountDeletionTombstoneEntry entry,
        AccountDeletionBinding binding) =>
        string.Equals(entry.AccountId, binding.AccountId, StringComparison.Ordinal)
        && string.Equals(
            entry.NormalizedEmail,
            binding.NormalizedEmail,
            StringComparison.Ordinal)
        && entry.ToEndpoints() == binding.Endpoints
        && string.Equals(
            entry.DeviceIdentifier,
            binding.DeviceIdentifier,
            StringComparison.Ordinal);

    private static bool SameDeletionScope(
        PersistedAccountDeletionTombstoneEntry entry,
        AccountDeletionBinding binding) =>
        string.Equals(entry.AccountId, binding.AccountId, StringComparison.Ordinal)
        && entry.ToEndpoints() == binding.Endpoints;

    private static bool BindingsMatch(
        AccountDeletionBinding left,
        AccountDeletionBinding right) =>
        string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal)
        && string.Equals(
            left.NormalizedEmail,
            right.NormalizedEmail,
            StringComparison.Ordinal)
        && left.Endpoints == right.Endpoints
        && string.Equals(
            left.DeviceIdentifier,
            right.DeviceIdentifier,
            StringComparison.Ordinal);

    private static bool IsRemovalAllowed(
        AccountDeletionStage stage,
        AccountDeletionTombstoneRemovalReason reason) => reason switch
    {
        AccountDeletionTombstoneRemovalReason.ServerRejectedPreparedRequest =>
            stage == AccountDeletionStage.Prepared,
        AccountDeletionTombstoneRemovalReason.CancelledBeforeDispatch =>
            stage == AccountDeletionStage.Prepared,
        _ => false,
    };

    private static void ValidateBinding(AccountDeletionBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var canonical = new AccountDeletionBinding(
            binding.AccountId,
            binding.NormalizedEmail,
            binding.Endpoints,
            binding.DeviceIdentifier);
        if (!BindingsMatch(canonical, binding))
        {
            throw new ArgumentException(
                "The account-deletion binding is not canonical.",
                nameof(binding));
        }
    }

    private void RequireSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "DPAPI account-deletion tombstone storage is only supported on Windows.");
        }
    }

    private static void EnsureLength(
        byte[] bytes,
        int maximumLength,
        string description)
    {
        if (bytes.Length is <= 0 || bytes.Length > maximumLength)
            throw new InvalidDataException($"{description} length is invalid.");
    }

    private static bool IsCorruptFailure(Exception exception) => exception is
        CryptographicException
        or JsonException
        or InvalidDataException
        or ArgumentException
        or FormatException
        or NotSupportedException and not PlatformNotSupportedException;

    private static bool IsUnavailableFailure(Exception exception) => exception is
        PlatformNotSupportedException
        or IOException
        or UnauthorizedAccessException;

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }
}

internal interface IAccountDeletionTombstoneProtectionPolicy
{
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[] entropy);

    byte[] Unprotect(byte[] protectedData, byte[] entropy);
}

internal sealed class CurrentUserDpapiAccountDeletionTombstoneProtection
    : IAccountDeletionTombstoneProtectionPolicy
{
    public static CurrentUserDpapiAccountDeletionTombstoneProtection Instance { get; } =
        new();

    private CurrentUserDpapiAccountDeletionTombstoneProtection()
    {
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only supported on Windows.");
        return ProtectedData.Protect(
            plaintext,
            entropy,
            DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only supported on Windows.");
        return ProtectedData.Unprotect(
            protectedData,
            entropy,
            DataProtectionScope.CurrentUser);
    }
}

internal interface IAccountDeletionTombstoneAtomicReplacePolicy
{
    bool IsSupported { get; }

    void Replace(string sourcePath, string destinationPath);
}

internal sealed partial class WindowsWriteThroughAccountDeletionTombstoneAtomicReplacePolicy
    : IAccountDeletionTombstoneAtomicReplacePolicy
{
    public static WindowsWriteThroughAccountDeletionTombstoneAtomicReplacePolicy Instance
        { get; } = new();

    private WindowsWriteThroughAccountDeletionTombstoneAtomicReplacePolicy()
    {
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Replace(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Write-through atomic replacement is only supported on Windows.");
        }

        if (!MoveFileEx(
                sourcePath,
                destinationPath,
                MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(
                "The account-deletion tombstone ledger could not be atomically replaced.",
                new Win32Exception(error));
        }
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x00000001,
        WriteThrough = 0x00000008,
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(
        string existingFileName,
        string newFileName,
        MoveFileFlags flags);
}
