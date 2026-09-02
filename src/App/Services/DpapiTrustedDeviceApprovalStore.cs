using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

public sealed partial class DpapiTrustedDeviceApprovalStore : ITrustedDeviceApprovalStore
{
    private const int MaximumFileLength = 256 * 1024;
    private const int MaximumPlaintextLength = 192 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;
    private const string EntropyDomain =
        "WinVaultWarden.TrustedDeviceApprovalLedgerProtection.v1";
    private const string PrivateKeyEntropyDomain =
        "WinVaultWarden.TrustedDeviceApprovalPrivateKeyProtection.v1";

    private static ReadOnlySpan<byte> FileMagic => "WVWAPR01"u8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private readonly ITrustedDeviceApprovalProtectionPolicy _protection;
    private readonly IAccountDeletionLocalWriteGuard _writeGuard;

    public DpapiTrustedDeviceApprovalStore(
        IAccountDeletionLocalWriteGuard writeGuard)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "trusted-device-approvals.bin"),
            CurrentUserDpapiTrustedDeviceApprovalProtection.Instance,
            writeGuard)
    {
    }

    public DpapiTrustedDeviceApprovalStore(string path)
        : this(
            path,
            CurrentUserDpapiTrustedDeviceApprovalProtection.Instance,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiTrustedDeviceApprovalStore(
        string path,
        ITrustedDeviceApprovalProtectionPolicy protection)
        : this(
            path,
            protection,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiTrustedDeviceApprovalStore(
        string path,
        ITrustedDeviceApprovalProtectionPolicy protection,
        IAccountDeletionLocalWriteGuard writeGuard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(writeGuard);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException(
                "The approval store path must have a parent directory.",
                nameof(path));
        _lockPath = _path + ".lock";
        _protection = protection;
        _writeGuard = writeGuard;
    }

    public TrustedDeviceApprovalStoreReadResult Read(DeviceKeyBinding binding) =>
        ReadCore(binding, requestId: null);

    public TrustedDeviceApprovalStoreReadResult Read(
        DeviceKeyBinding binding,
        string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var normalizedRequestId = Guid.TryParseExact(requestId, "D", out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException(
                "The approval request identifier must be a canonical GUID.",
                nameof(requestId));
        return ReadCore(binding, normalizedRequestId);
    }

    public ITrustedDeviceApprovalIntentLease? TryAcquireBeginIntent(
        DeviceKeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateBinding(binding);
        RequireSupported();
        var scopeName = Path.GetFileNameWithoutExtension(binding.CreateStorageFileName());
        var intentPath = Path.Combine(
            _directoryPath,
            $".{Path.GetFileName(_path)}.{scopeName}.begin.lock");
        FileStream? intentStream = null;
        try
        {
            Directory.CreateDirectory(_directoryPath);
            try
            {
                intentStream = new FileStream(
                    intentPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                return null;
            }

            lock (_gate)
            {
                PersistedTrustedDeviceApprovalLedger? ledger = null;
                try
                {
                    using var storeLock = AcquireStoreLock();
                    ledger = ReadLedgerForMutation();
                    if (ledger.Entries.Any(entry => SameBinding(entry, binding)))
                        return null;
                }
                finally
                {
                    ledger?.ClearSecrets();
                }
            }

            var lease = new TrustedDeviceApprovalIntentLease(
                this,
                binding,
                intentStream);
            intentStream = null;
            return lease;
        }
        finally
        {
            intentStream?.Dispose();
        }
    }

    public TrustedDeviceApprovalStoreRecord? TryCreate(
        ITrustedDeviceApprovalIntentLease intent,
        TrustedDeviceApprovalPending pending)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(pending);
        if (intent is not TrustedDeviceApprovalIntentLease lease)
        {
            throw new ArgumentException(
                "The approval intent was not issued by this store.",
                nameof(intent));
        }
        return lease.Execute(this, pending.Binding, () =>
        {
            using var writeLease = _writeGuard.AcquireOrThrow(
                pending.Binding.AccountId,
                pending.Binding.Endpoints);
            return TryCreateCore(pending);
        });
    }

    private TrustedDeviceApprovalStoreRecord? TryCreateCore(
        TrustedDeviceApprovalPending pending)
    {
        RequireSupported();
        var entryId = Guid.NewGuid();
        byte[]? privateKey = null;
        byte[]? privateKeyEntropy = null;
        byte[]? protectedPrivateKey = null;
        PersistedTrustedDeviceApprovalEntry? newEntry = null;

        try
        {
            privateKey = pending.CopyPrivateKeyPkcs8();
            privateKeyEntropy = CreatePrivateKeyProtectionEntropy(entryId, pending);
            protectedPrivateKey = ProtectOwned(privateKey, privateKeyEntropy);
            EnsureLength(
                protectedPrivateKey,
                PersistedTrustedDeviceApprovalEntry.MaximumProtectedPrivateKeyLength,
                "Protected approval private key");
            newEntry = PersistedTrustedDeviceApprovalEntry.Create(
                entryId,
                revision: 1,
                pending,
                protectedPrivateKey);

            lock (_gate)
            {
                PersistedTrustedDeviceApprovalLedger? ledger = null;
                try
                {
                    using var storeLock = AcquireStoreLock();
                    ledger = ReadLedgerForMutation();
                    if (ledger.Entries.Any(entry =>
                            SameBinding(entry, pending.Binding)
                            || string.Equals(
                                entry.RequestId,
                                pending.RequestId,
                                StringComparison.Ordinal)))
                    {
                        return null;
                    }
                    if (ledger.Entries.Count >= PersistedTrustedDeviceApprovalLedger.MaximumEntries)
                        throw new InvalidDataException("The approval ledger is full.");

                    var updated = new PersistedTrustedDeviceApprovalLedger(
                        PersistedTrustedDeviceApprovalLedger.FormatName,
                        PersistedTrustedDeviceApprovalLedger.CurrentSchemaVersion,
                        ledger.Entries.Append(newEntry).ToArray());
                    WriteLedger(updated);
                    TryDeleteTemporaryFiles();
                    return new TrustedDeviceApprovalStoreRecord(
                        entryId,
                        revision: 1,
                        pending.Clone());
                }
                finally
                {
                    ledger?.ClearSecrets();
                }
            }
        }
        finally
        {
            newEntry?.ClearSecret();
            Clear(protectedPrivateKey);
            Clear(privateKeyEntropy);
            Clear(privateKey);
        }
    }

    public bool Remove(
        TrustedDeviceApprovalStoreRecord expected,
        TrustedDeviceApprovalRemovalReason reason)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        RequireSupported();

        var removed = false;
        lock (_gate)
        {
            PersistedTrustedDeviceApprovalLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                ledger = ReadLedgerForMutation();
                var index = FindIndex(ledger.Entries, expected);
                if (index < 0)
                    return false;

                var remaining = ledger.Entries
                    .Where((_, candidateIndex) => candidateIndex != index)
                    .ToArray();
                WriteLedger(new PersistedTrustedDeviceApprovalLedger(
                    PersistedTrustedDeviceApprovalLedger.FormatName,
                    PersistedTrustedDeviceApprovalLedger.CurrentSchemaVersion,
                    remaining));
                TryDeleteTemporaryFiles();
                removed = true;
            }
            finally
            {
                ledger?.ClearSecrets();
            }
        }

        if (removed)
            expected.Dispose();
        return removed;
    }

    public AccountScopedRemovalStatus RemoveAccountScope(
        string accountId,
        ServerEndpoints endpoints)
    {
        ValidateAccountScope(accountId, endpoints);
        if (!_protection.IsSupported)
            return AccountScopedRemovalStatus.Unavailable;

        lock (_gate)
        {
            PersistedTrustedDeviceApprovalLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                ledger = ReadLedgerForMutation();
                ValidateEntrySecrets(ledger.Entries);
                var remaining = ledger.Entries
                    .Where(entry => !SameAccountScope(entry, accountId, endpoints))
                    .ToArray();
                var removed = remaining.Length != ledger.Entries.Count;
                if (removed)
                {
                    try
                    {
                        WriteLedger(new PersistedTrustedDeviceApprovalLedger(
                            PersistedTrustedDeviceApprovalLedger.FormatName,
                            PersistedTrustedDeviceApprovalLedger.CurrentSchemaVersion,
                            remaining));
                    }
                    catch (CryptographicException exception)
                    {
                        throw new IOException(
                            "The approval ledger could not be protected for rewriting.",
                            exception);
                    }
                }
                DeleteTemporaryFilesStrict();
                return removed
                    ? AccountScopedRemovalStatus.Removed
                    : AccountScopedRemovalStatus.Absent;
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return AccountScopedRemovalStatus.Corrupt;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return AccountScopedRemovalStatus.Unavailable;
            }
            finally
            {
                ledger?.ClearSecrets();
            }
        }
    }

    private TrustedDeviceApprovalStoreReadResult ReadCore(
        DeviceKeyBinding binding,
        string? requestId)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateBinding(binding);
        if (!_protection.IsSupported)
            return TrustedDeviceApprovalStoreReadResult.Unavailable;

        lock (_gate)
        {
            PersistedTrustedDeviceApprovalLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                ledger = ReadLedgerForMutation();
                var entry = ledger.Entries.FirstOrDefault(candidate =>
                    SameBinding(candidate, binding)
                    && (requestId is null
                        || string.Equals(candidate.RequestId, requestId, StringComparison.Ordinal)));
                return entry is null
                    ? TrustedDeviceApprovalStoreReadResult.Absent
                    : TrustedDeviceApprovalStoreReadResult.Found(CreateRecord(entry));
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return TrustedDeviceApprovalStoreReadResult.Corrupt;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return TrustedDeviceApprovalStoreReadResult.Unavailable;
            }
            finally
            {
                ledger?.ClearSecrets();
            }
        }
    }

    private PersistedTrustedDeviceApprovalLedger ReadLedgerForMutation()
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
                return PersistedTrustedDeviceApprovalLedger.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return PersistedTrustedDeviceApprovalLedger.Empty;
            }

            if (fileBytes.Length <= FileMagic.Length
                || !fileBytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
            {
                throw new InvalidDataException("The approval ledger header is invalid.");
            }

            protectedBytes = fileBytes.AsSpan(FileMagic.Length).ToArray();
            entropy = CreateProtectionEntropy();
            plaintext = UnprotectOwned(
                protectedBytes,
                entropy,
                MaximumPlaintextLength,
                "Approval ledger plaintext");
            var ledger = JsonSerializer.Deserialize(
                plaintext,
                AppJsonContext.Default.PersistedTrustedDeviceApprovalLedger);
            if (ledger is null || !ledger.TryValidate())
            {
                ledger?.ClearSecrets();
                throw new InvalidDataException("The approval ledger schema is invalid.");
            }
            return ledger;
        }
        finally
        {
            Clear(plaintext);
            Clear(entropy);
            Clear(protectedBytes);
            Clear(fileBytes);
        }
    }

    private void WriteLedger(PersistedTrustedDeviceApprovalLedger ledger)
    {
        if (!ledger.TryValidate())
            throw new InvalidDataException("The approval ledger is invalid.");

        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                ledger,
                AppJsonContext.Default.PersistedTrustedDeviceApprovalLedger);
            EnsureLength(plaintext, MaximumPlaintextLength, "Approval ledger plaintext");
            entropy = CreateProtectionEntropy();
            protectedBytes = ProtectOwned(plaintext, entropy);
            EnsureLength(
                protectedBytes,
                MaximumFileLength - FileMagic.Length,
                "Protected approval ledger");
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
                throw new IOException("Timed out acquiring the approval store lock.");
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
            throw new InvalidDataException("The approval ledger file length is invalid.");

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("The approval ledger changed while being read.");
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
            throw new CryptographicException("The approval protector returned no payload.");
        if (ReferenceEquals(protectedBytes, plaintext)
            || ReferenceEquals(protectedBytes, entropy))
        {
            throw new CryptographicException("The approval protector returned an aliased payload.");
        }
        return protectedBytes;
    }

    private byte[] UnprotectOwned(
        byte[] protectedBytes,
        byte[] entropy,
        int maximumLength,
        string description)
    {
        var plaintext = _protection.Unprotect(protectedBytes, entropy);
        if (plaintext is null)
            throw new CryptographicException("The approval protector returned no plaintext.");
        var owned = ReferenceEquals(plaintext, protectedBytes)
            || ReferenceEquals(plaintext, entropy)
                ? plaintext.ToArray()
                : plaintext;
        try
        {
            EnsureLength(owned, maximumLength, description);
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
        var framed = GC.AllocateUninitializedArray<byte>(FileMagic.Length + protectedBytes.Length);
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

            if (OperatingSystem.IsWindows())
            {
                if (!MoveFileEx(
                        temporaryPath,
                        _path,
                        MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough))
                {
                    var error = Marshal.GetLastPInvokeError();
                    throw new IOException(
                        "The approval ledger could not be atomically replaced.",
                        new Win32Exception(error));
                }
            }
            else
            {
                File.Move(temporaryPath, _path, overwrite: true);
            }
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

    private void DeleteTemporaryFilesStrict()
    {
        foreach (var path in Directory.EnumerateFiles(
            _directoryPath,
            $".{Path.GetFileName(_path)}.*.tmp"))
        {
            File.Delete(path);
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

    private static int FindIndex(
        IReadOnlyList<PersistedTrustedDeviceApprovalEntry> entries,
        TrustedDeviceApprovalStoreRecord expected)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var pending = expected.Pending;
            if (entry.EntryId == expected.EntryId
                && entry.Revision == expected.Revision
                && string.Equals(entry.RequestId, pending.RequestId, StringComparison.Ordinal)
                && SameBinding(entry, pending.Binding)
                && entry.CreatedAt == pending.CreatedAt
                && entry.ExpiresAt == pending.ExpiresAt
                && entry.RequestType == (int)pending.RequestType)
            {
                return index;
            }
        }
        return -1;
    }

    private TrustedDeviceApprovalStoreRecord CreateRecord(
        PersistedTrustedDeviceApprovalEntry entry)
    {
        byte[]? entropy = null;
        byte[]? privateKey = null;
        try
        {
            entropy = CreatePrivateKeyProtectionEntropy(entry);
            privateKey = UnprotectOwned(
                entry.ProtectedPrivateKey,
                entropy,
                TrustedDeviceApprovalPending.MaximumPrivateKeyLength,
                "Approval private key");
            return entry.ToRecord(privateKey);
        }
        finally
        {
            Clear(privateKey);
            Clear(entropy);
        }
    }

    private void ValidateEntrySecrets(
        IEnumerable<PersistedTrustedDeviceApprovalEntry> entries)
    {
        foreach (var entry in entries)
        {
            using var record = CreateRecord(entry);
        }
    }

    private static byte[] CreatePrivateKeyProtectionEntropy(
        Guid entryId,
        TrustedDeviceApprovalPending pending) =>
        CreatePrivateKeyProtectionEntropy(
            entryId,
            pending.RequestId,
            pending.Binding,
            pending.CreatedAt,
            pending.ExpiresAt,
            pending.RequestType);

    private static byte[] CreatePrivateKeyProtectionEntropy(
        PersistedTrustedDeviceApprovalEntry entry) =>
        CreatePrivateKeyProtectionEntropy(
            entry.EntryId,
            entry.RequestId,
            new DeviceKeyBinding(
                entry.AccountId,
                entry.ToEndpoints(),
                entry.DeviceIdentifier),
            entry.CreatedAt,
            entry.ExpiresAt,
            (TrustedDeviceApprovalRequestType)entry.RequestType);

    private static byte[] CreatePrivateKeyProtectionEntropy(
        Guid entryId,
        string requestId,
        DeviceKeyBinding binding,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        TrustedDeviceApprovalRequestType requestType)
    {
        var descriptor = string.Join(
            "\n",
            PrivateKeyEntropyDomain,
            entryId.ToString("N"),
            requestId,
            binding.AccountId,
            binding.Endpoints.WebVault.Value,
            binding.Endpoints.Api.Value,
            binding.Endpoints.Identity.Value,
            binding.Endpoints.Notifications.Value,
            binding.Endpoints.Icons.Value,
            binding.Endpoints.SendWeb.Value,
            binding.DeviceIdentifier,
            createdAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            expiresAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            ((int)requestType).ToString(CultureInfo.InvariantCulture));
        var bytes = Encoding.UTF8.GetBytes(descriptor);
        try
        {
            return SHA256.HashData(bytes);
        }
        finally
        {
            Clear(bytes);
        }
    }

    private static bool SameBinding(
        PersistedTrustedDeviceApprovalEntry entry,
        DeviceKeyBinding binding) =>
        string.Equals(entry.AccountId, binding.AccountId, StringComparison.Ordinal)
        && entry.ToEndpoints() == binding.Endpoints
        && string.Equals(
            entry.DeviceIdentifier,
            binding.DeviceIdentifier,
            StringComparison.Ordinal);

    private static bool SameAccountScope(
        PersistedTrustedDeviceApprovalEntry entry,
        string accountId,
        ServerEndpoints endpoints) =>
        string.Equals(entry.AccountId, accountId, StringComparison.Ordinal)
        && entry.ToEndpoints() == endpoints;

    private static void ValidateAccountScope(
        string accountId,
        ServerEndpoints endpoints)
    {
        var binding = new DeviceKeyBinding(
            accountId,
            endpoints,
            Guid.Empty.ToString("D"));
        ValidateBinding(binding);
    }

    private static void ValidateBinding(DeviceKeyBinding binding)
    {
        if (!Guid.TryParseExact(binding.DeviceIdentifier, "D", out _)
            || new[]
            {
                binding.Endpoints.WebVault.Value,
                binding.Endpoints.Api.Value,
                binding.Endpoints.Identity.Value,
                binding.Endpoints.Notifications.Value,
                binding.Endpoints.Icons.Value,
                binding.Endpoints.SendWeb.Value,
            }.Any(value => value.Length > TrustedDeviceApprovalPending.MaximumEndpointLength))
        {
            throw new ArgumentException("The approval binding is invalid.", nameof(binding));
        }
    }

    private void RequireSupported()
    {
        if (!_protection.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "DPAPI trusted-device approval storage is only supported on Windows.");
        }
    }

    private static void EnsureLength(byte[] bytes, int maximumLength, string description)
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
        or UnauthorizedAccessException
        or System.Security.SecurityException;

    private static bool IsLockContention(IOException exception)
    {
        var errorCode = exception.HResult & 0xffff;
        return OperatingSystem.IsWindows()
            ? errorCode is 32 or 33
            : errorCode == 11;
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }

    private sealed class TrustedDeviceApprovalIntentLease
        : ITrustedDeviceApprovalIntentLease
    {
        private readonly object _gate = new();
        private readonly DpapiTrustedDeviceApprovalStore _owner;
        private FileStream? _stream;

        public TrustedDeviceApprovalIntentLease(
            DpapiTrustedDeviceApprovalStore owner,
            DeviceKeyBinding binding,
            FileStream stream)
        {
            _owner = owner;
            Binding = binding;
            _stream = stream;
        }

        public DeviceKeyBinding Binding { get; }

        public T Execute<T>(
            DpapiTrustedDeviceApprovalStore owner,
            DeviceKeyBinding binding,
            Func<T> action)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stream is null, this);
                if (!ReferenceEquals(_owner, owner)
                    || Binding != binding)
                {
                    throw new ArgumentException(
                        "The approval intent does not match this store and binding.");
                }
                return action();
            }
        }

        public void Dispose()
        {
            FileStream? stream;
            lock (_gate)
            {
                stream = _stream;
                _stream = null;
            }
            stream?.Dispose();
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

internal interface ITrustedDeviceApprovalProtectionPolicy
{
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[] entropy);

    byte[] Unprotect(byte[] protectedData, byte[] entropy);
}

internal sealed class CurrentUserDpapiTrustedDeviceApprovalProtection
    : ITrustedDeviceApprovalProtectionPolicy
{
    public static CurrentUserDpapiTrustedDeviceApprovalProtection Instance { get; } = new();

    private CurrentUserDpapiTrustedDeviceApprovalProtection()
    {
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only supported on Windows.");
        return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only supported on Windows.");
        return ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser);
    }
}
