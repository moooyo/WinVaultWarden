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

public sealed partial class DpapiTdeV2JitPendingStore : ITdeV2JitPendingStore
{
    private const int MaximumFileLength = 2 * 1024 * 1024;
    private const int MaximumPlaintextLength = 1536 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;
    private const string EntropyDomain =
        "WinVaultWarden.TdeV2JitPendingLedgerProtection.v1";
    private const string UserKeyEntropyDomain =
        "WinVaultWarden.TdeV2JitPendingUserKeyProtection.v1";
    private const string DeviceKeyEntropyDomain =
        "WinVaultWarden.TdeV2JitPendingDeviceKeyProtection.v1";

    private static ReadOnlySpan<byte> FileMagic => "WVWTJ201"u8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private readonly ITdeV2JitPendingProtectionPolicy _protection;
    private readonly IAccountDeletionLocalWriteGuard _writeGuard;

    public DpapiTdeV2JitPendingStore(IAccountDeletionLocalWriteGuard writeGuard)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "tde-v2-jit-pending.bin"),
            CurrentUserDpapiTdeV2JitPendingProtection.Instance,
            writeGuard)
    {
    }

    internal DpapiTdeV2JitPendingStore(string path)
        : this(
            path,
            CurrentUserDpapiTdeV2JitPendingProtection.Instance,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiTdeV2JitPendingStore(
        string path,
        ITdeV2JitPendingProtectionPolicy protection)
        : this(
            path,
            protection,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiTdeV2JitPendingStore(
        string path,
        ITdeV2JitPendingProtectionPolicy protection,
        IAccountDeletionLocalWriteGuard writeGuard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(writeGuard);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException(
                "The V2 JIT pending-store path must have a parent directory.",
                nameof(path));
        _lockPath = _path + ".lock";
        _protection = protection;
        _writeGuard = writeGuard;
    }

    public TdeV2JitPendingReadResult Read(DeviceKeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateBinding(binding);
        if (!_protection.IsSupported)
            return TdeV2JitPendingReadResult.Unavailable;

        lock (_gate)
        {
            PersistedTdeV2JitPendingLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                ledger = ReadLedgerForMutation();
                var entry = ledger.Entries.FirstOrDefault(candidate =>
                    SameBinding(candidate, binding));
                return entry is null
                    ? TdeV2JitPendingReadResult.Absent
                    : TdeV2JitPendingReadResult.Found(CreateRecord(entry));
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return TdeV2JitPendingReadResult.Corrupt;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return TdeV2JitPendingReadResult.Unavailable;
            }
            finally
            {
                ledger?.ClearSecrets();
            }
        }
    }

    public TdeV2JitPendingStoreRecord? TryCreate(TdeV2JitPending pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        RequireSupported();
        var entryId = Guid.NewGuid();
        byte[]? userKeyTransport = null;
        byte[]? deviceKey = null;
        byte[]? userKeyEntropy = null;
        byte[]? deviceKeyEntropy = null;
        byte[]? protectedUserKey = null;
        byte[]? protectedDeviceKey = null;
        PersistedTdeV2JitPendingEntry? newEntry = null;

        try
        {
            userKeyTransport = pending.CopyUserKeyTransport();
            deviceKey = pending.CopyDeviceKey();
            userKeyEntropy = CreateSecretProtectionEntropy(
                entryId,
                pending,
                UserKeyEntropyDomain);
            protectedUserKey = ProtectOwned(userKeyTransport, userKeyEntropy);
            EnsureLength(
                protectedUserKey,
                PersistedTdeV2JitPendingEntry.MaximumProtectedSecretLength,
                "Protected V2 user key");
            if (deviceKey is not null)
            {
                deviceKeyEntropy = CreateSecretProtectionEntropy(
                    entryId,
                    pending,
                    DeviceKeyEntropyDomain);
                protectedDeviceKey = ProtectOwned(deviceKey, deviceKeyEntropy);
                EnsureLength(
                    protectedDeviceKey,
                    PersistedTdeV2JitPendingEntry.MaximumProtectedSecretLength,
                    "Protected trusted-device key");
            }

            newEntry = PersistedTdeV2JitPendingEntry.Create(
                entryId,
                revision: 1,
                TdeV2JitPendingStage.Prepared,
                pending,
                protectedUserKey,
                protectedDeviceKey);

            using var writeLease = _writeGuard.AcquireOrThrow(
                pending.Binding.AccountId,
                pending.Binding.Endpoints);
            lock (_gate)
            {
                PersistedTdeV2JitPendingLedger? ledger = null;
                try
                {
                    using var storeLock = AcquireStoreLock();
                    ledger = ReadLedgerForMutation();
                    if (ledger.Entries.Any(entry => SameBinding(entry, pending.Binding)))
                        return null;
                    if (ledger.Entries.Count >= PersistedTdeV2JitPendingLedger.MaximumEntries)
                        throw new InvalidDataException("The V2 JIT pending ledger is full.");

                    WriteLedger(new PersistedTdeV2JitPendingLedger(
                        PersistedTdeV2JitPendingLedger.FormatName,
                        PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                        ledger.Entries.Append(newEntry).ToArray()));
                    TryDeleteTemporaryFiles();
                    return new TdeV2JitPendingStoreRecord(
                        entryId,
                        revision: 1,
                        TdeV2JitPendingStage.Prepared,
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
            newEntry?.ClearSecrets();
            Clear(protectedDeviceKey);
            Clear(protectedUserKey);
            Clear(deviceKeyEntropy);
            Clear(userKeyEntropy);
            Clear(deviceKey);
            Clear(userKeyTransport);
        }
    }

    public TdeV2JitPendingStoreRecord? TryAdvance(
        TdeV2JitPendingStoreRecord expected,
        TdeV2JitPendingStage nextStage)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Enum.IsDefined(nextStage))
            throw new ArgumentOutOfRangeException(nameof(nextStage));
        if (nextStage <= expected.Stage)
        {
            throw new ArgumentException(
                "A V2 JIT pending stage update must be strictly monotonic.",
                nameof(nextStage));
        }
        RequireSupported();
        using var writeLease = _writeGuard.AcquireOrThrow(
            expected.Pending.Binding.AccountId,
            expected.Pending.Binding.Endpoints);

        TdeV2JitPending? nextPending = expected.Pending.Clone();
        try
        {
            lock (_gate)
            {
                PersistedTdeV2JitPendingLedger? ledger = null;
                try
                {
                    using var storeLock = AcquireStoreLock();
                    ledger = ReadLedgerForMutation();
                    var index = FindIndex(ledger.Entries, expected);
                    if (index < 0)
                        return null;

                    var entry = ledger.Entries[index];
                    if (!SecretMaterialMatches(entry, expected.Pending))
                        return null;
                    var revision = checked(entry.Revision + 1);
                    var updatedEntry = entry with
                    {
                        Revision = revision,
                        Stage = (int)nextStage,
                    };
                    var updatedEntries = ledger.Entries.ToArray();
                    updatedEntries[index] = updatedEntry;
                    WriteLedger(new PersistedTdeV2JitPendingLedger(
                        PersistedTdeV2JitPendingLedger.FormatName,
                        PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                        updatedEntries));
                    TryDeleteTemporaryFiles();

                    var result = new TdeV2JitPendingStoreRecord(
                        entry.EntryId,
                        revision,
                        nextStage,
                        nextPending);
                    nextPending = null;
                    expected.Dispose();
                    return result;
                }
                finally
                {
                    ledger?.ClearSecrets();
                }
            }
        }
        finally
        {
            nextPending?.Dispose();
        }
    }

    public bool Remove(
        TdeV2JitPendingStoreRecord expected,
        TdeV2JitPendingRemovalReason reason)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        if (reason == TdeV2JitPendingRemovalReason.Completed
            && expected.Stage != TdeV2JitPendingStage.LocalCommitReady)
        {
            return false;
        }
        RequireSupported();

        var removed = false;
        lock (_gate)
        {
            PersistedTdeV2JitPendingLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                ledger = ReadLedgerForMutation();
                var index = FindIndex(ledger.Entries, expected);
                if (index < 0 || !SecretMaterialMatches(ledger.Entries[index], expected.Pending))
                    return false;

                WriteLedger(new PersistedTdeV2JitPendingLedger(
                    PersistedTdeV2JitPendingLedger.FormatName,
                    PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                    ledger.Entries
                        .Where((_, candidateIndex) => candidateIndex != index)
                        .ToArray()));
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
            PersistedTdeV2JitPendingLedger? ledger = null;
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
                        WriteLedger(new PersistedTdeV2JitPendingLedger(
                            PersistedTdeV2JitPendingLedger.FormatName,
                            PersistedTdeV2JitPendingLedger.CurrentSchemaVersion,
                            remaining));
                    }
                    catch (CryptographicException exception)
                    {
                        throw new IOException(
                            "The V2 JIT pending ledger could not be protected for rewriting.",
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

    private PersistedTdeV2JitPendingLedger ReadLedgerForMutation()
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
                return PersistedTdeV2JitPendingLedger.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return PersistedTdeV2JitPendingLedger.Empty;
            }

            if (fileBytes.Length <= FileMagic.Length
                || !fileBytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
            {
                throw new InvalidDataException("The V2 JIT pending ledger header is invalid.");
            }

            protectedBytes = fileBytes.AsSpan(FileMagic.Length).ToArray();
            entropy = CreateLedgerProtectionEntropy();
            plaintext = UnprotectOwned(
                protectedBytes,
                entropy,
                MaximumPlaintextLength,
                "V2 JIT pending ledger plaintext");
            var ledger = JsonSerializer.Deserialize(
                plaintext,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            if (ledger is null || !ledger.TryValidate())
            {
                ledger?.ClearSecrets();
                throw new InvalidDataException("The V2 JIT pending ledger schema is invalid.");
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

    private void WriteLedger(PersistedTdeV2JitPendingLedger ledger)
    {
        if (!ledger.TryValidate())
            throw new InvalidDataException("The V2 JIT pending ledger is invalid.");

        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                ledger,
                AppJsonContext.Default.PersistedTdeV2JitPendingLedger);
            EnsureLength(plaintext, MaximumPlaintextLength, "V2 JIT pending ledger plaintext");
            entropy = CreateLedgerProtectionEntropy();
            protectedBytes = ProtectOwned(plaintext, entropy);
            EnsureLength(
                protectedBytes,
                MaximumFileLength - FileMagic.Length,
                "Protected V2 JIT pending ledger");
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
                throw new IOException("Timed out acquiring the V2 JIT pending-store lock.");
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
            throw new InvalidDataException("The V2 JIT pending ledger file length is invalid.");

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("The V2 JIT pending ledger changed while being read.");
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
            throw new CryptographicException("The V2 JIT protector returned no payload.");
        if (ReferenceEquals(protectedBytes, plaintext)
            || ReferenceEquals(protectedBytes, entropy))
        {
            throw new CryptographicException("The V2 JIT protector returned an aliased payload.");
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
            throw new CryptographicException("The V2 JIT protector returned no plaintext.");
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

    private static byte[] CreateLedgerProtectionEntropy()
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
                        "The V2 JIT pending ledger could not be atomically replaced.",
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

    private TdeV2JitPendingStoreRecord CreateRecord(PersistedTdeV2JitPendingEntry entry)
    {
        byte[]? userKeyEntropy = null;
        byte[]? deviceKeyEntropy = null;
        byte[]? userKeyTransport = null;
        byte[]? deviceKey = null;
        try
        {
            userKeyEntropy = CreateSecretProtectionEntropy(entry, UserKeyEntropyDomain);
            userKeyTransport = UnprotectOwned(
                entry.ProtectedUserKeyTransport,
                userKeyEntropy,
                TdeV2JitPending.MaximumUserKeyTransportLength,
                "V2 user-key transport");
            if (entry.TrustCurrentDevice)
            {
                deviceKeyEntropy = CreateSecretProtectionEntropy(entry, DeviceKeyEntropyDomain);
                deviceKey = UnprotectOwned(
                    entry.ProtectedDeviceKey,
                    deviceKeyEntropy,
                    TdeV2JitPending.DeviceKeyLength,
                    "Trusted-device key");
            }

            return entry.ToRecord(
                userKeyTransport,
                deviceKey is null ? ReadOnlySpan<byte>.Empty : deviceKey);
        }
        finally
        {
            Clear(deviceKey);
            Clear(userKeyTransport);
            Clear(deviceKeyEntropy);
            Clear(userKeyEntropy);
        }
    }

    private void ValidateEntrySecrets(IEnumerable<PersistedTdeV2JitPendingEntry> entries)
    {
        foreach (var entry in entries)
        {
            using var record = CreateRecord(entry);
        }
    }

    private bool SecretMaterialMatches(
        PersistedTdeV2JitPendingEntry entry,
        TdeV2JitPending expected)
    {
        using var current = CreateRecord(entry);
        byte[]? expectedUserKey = null;
        byte[]? currentUserKey = null;
        byte[]? expectedDeviceKey = null;
        byte[]? currentDeviceKey = null;
        try
        {
            expectedUserKey = expected.CopyUserKeyTransport();
            currentUserKey = current.Pending.CopyUserKeyTransport();
            expectedDeviceKey = expected.CopyDeviceKey();
            currentDeviceKey = current.Pending.CopyDeviceKey();
            return FixedTimeEquals(expectedUserKey, currentUserKey)
                && FixedTimeEquals(expectedDeviceKey, currentDeviceKey);
        }
        finally
        {
            Clear(currentDeviceKey);
            Clear(expectedDeviceKey);
            Clear(currentUserKey);
            Clear(expectedUserKey);
        }
    }

    private static bool FixedTimeEquals(byte[]? first, byte[]? second)
    {
        if (first is null || second is null)
            return first is null && second is null;
        return first.Length == second.Length
            && CryptographicOperations.FixedTimeEquals(first, second);
    }

    private static int FindIndex(
        IReadOnlyList<PersistedTdeV2JitPendingEntry> entries,
        TdeV2JitPendingStoreRecord expected)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var pending = expected.Pending;
            if (entry.EntryId == expected.EntryId
                && entry.Revision == expected.Revision
                && entry.Stage == (int)expected.Stage
                && SameBinding(entry, pending.Binding)
                && string.Equals(
                    entry.OrganizationId,
                    pending.OrganizationId,
                    StringComparison.Ordinal)
                && string.Equals(
                    entry.OrganizationIdentifier,
                    pending.OrganizationIdentifier,
                    StringComparison.Ordinal)
                && entry.CreatedAt == pending.CreatedAt
                && string.Equals(
                    entry.ResetPasswordKey,
                    pending.ResetPasswordKey,
                    StringComparison.Ordinal)
                && entry.TrustCurrentDevice == pending.TrustCurrentDevice
                && entry.ToAccountKeysRequest() == pending.AccountKeysRequest
                && entry.ToTrustedDeviceRequest() == pending.TrustedDeviceRequest)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool SameBinding(
        PersistedTdeV2JitPendingEntry entry,
        DeviceKeyBinding binding) =>
        string.Equals(entry.AccountId, binding.AccountId, StringComparison.Ordinal)
        && entry.ToEndpoints() == binding.Endpoints
        && string.Equals(
            entry.DeviceIdentifier,
            binding.DeviceIdentifier,
            StringComparison.Ordinal);

    private static bool SameAccountScope(
        PersistedTdeV2JitPendingEntry entry,
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
        if (!IsCanonicalGuid(binding.AccountId)
            || !IsCanonicalGuid(binding.DeviceIdentifier)
            || new[]
            {
                binding.Endpoints.WebVault.Value,
                binding.Endpoints.Api.Value,
                binding.Endpoints.Identity.Value,
                binding.Endpoints.Notifications.Value,
                binding.Endpoints.Icons.Value,
                binding.Endpoints.SendWeb.Value,
            }.Any(value => value.Length > TdeV2JitPending.MaximumEndpointLength))
        {
            throw new ArgumentException("The V2 JIT pending binding is invalid.", nameof(binding));
        }
    }

    private static bool IsCanonicalGuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed)
        && string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal);

    private static byte[] CreateSecretProtectionEntropy(
        Guid entryId,
        TdeV2JitPending pending,
        string domain)
    {
        var request = pending.AccountKeysRequest;
        var accountKeys = request.AccountKeys;
        var encryption = accountKeys.PublicKeyEncryptionKeyPair;
        var signature = accountKeys.SignatureKeyPair;
        var security = accountKeys.SecurityState;
        var trusted = pending.TrustedDeviceRequest;
        return HashDescriptor(
            domain,
            entryId,
            [
                pending.AccountId,
                pending.Endpoints.WebVault.Value,
                pending.Endpoints.Api.Value,
                pending.Endpoints.Identity.Value,
                pending.Endpoints.Notifications.Value,
                pending.Endpoints.Icons.Value,
                pending.Endpoints.SendWeb.Value,
                pending.DeviceIdentifier,
                pending.OrganizationId,
                pending.OrganizationIdentifier,
                pending.CreatedAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
                request.PublicKey,
                request.EncryptedPrivateKey,
                accountKeys.UserKeyEncryptedAccountPrivateKey,
                accountKeys.AccountPublicKey,
                encryption.WrappedPrivateKey,
                encryption.PublicKey,
                encryption.SignedPublicKey,
                signature.SignatureAlgorithm,
                signature.WrappedSigningKey,
                signature.VerifyingKey,
                security.SecurityState,
                security.SecurityVersion.ToString(CultureInfo.InvariantCulture),
                request.UserKeyId,
                pending.ResetPasswordKey,
                pending.TrustCurrentDevice ? "1" : "0",
                trusted?.EncryptedUserKey ?? string.Empty,
                trusted?.EncryptedPublicKey ?? string.Empty,
                trusted?.EncryptedPrivateKey ?? string.Empty,
            ]);
    }

    private static byte[] CreateSecretProtectionEntropy(
        PersistedTdeV2JitPendingEntry entry,
        string domain) => HashDescriptor(
        domain,
        entry.EntryId,
        [
            entry.AccountId,
            entry.WebVault,
            entry.Api,
            entry.Identity,
            entry.Notifications,
            entry.Icons,
            entry.SendWeb,
            entry.DeviceIdentifier,
            entry.OrganizationId,
            entry.OrganizationIdentifier,
            entry.CreatedAt.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            entry.PublicKey,
            entry.EncryptedPrivateKey,
            entry.UserKeyEncryptedAccountPrivateKey,
            entry.AccountPublicKey,
            entry.PublicKeyEncryptionWrappedPrivateKey,
            entry.PublicKeyEncryptionPublicKey,
            entry.PublicKeyEncryptionSignedPublicKey,
            entry.SignatureAlgorithm,
            entry.WrappedSigningKey,
            entry.VerifyingKey,
            entry.SecurityState,
            entry.SecurityVersion.ToString(CultureInfo.InvariantCulture),
            entry.UserKeyId,
            entry.ResetPasswordKey,
            entry.TrustCurrentDevice ? "1" : "0",
            entry.TrustedDeviceEncryptedUserKey ?? string.Empty,
            entry.TrustedDeviceEncryptedPublicKey ?? string.Empty,
            entry.TrustedDeviceEncryptedPrivateKey ?? string.Empty,
        ]);

    private static byte[] HashDescriptor(
        string domain,
        Guid entryId,
        string[] values)
    {
        var descriptor = string.Join(
            "\n",
            values.Prepend(entryId.ToString("N")).Prepend(domain));
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

    private void RequireSupported()
    {
        if (!_protection.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "DPAPI V2 JIT pending storage is only supported on Windows.");
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

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
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

internal interface ITdeV2JitPendingProtectionPolicy
{
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[] entropy);

    byte[] Unprotect(byte[] protectedData, byte[] entropy);
}

internal sealed class CurrentUserDpapiTdeV2JitPendingProtection
    : ITdeV2JitPendingProtectionPolicy
{
    public static CurrentUserDpapiTdeV2JitPendingProtection Instance { get; } = new();

    private CurrentUserDpapiTdeV2JitPendingProtection()
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
