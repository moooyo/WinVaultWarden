using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

public sealed partial class DpapiCipherCreatePendingStore : ICipherCreatePendingStore
{
    private const int MaximumFileLength = 4 * 1024 * 1024;
    private const int MaximumPlaintextLength = 3 * 1024 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;
    private const string EntropyDomain = "WinVaultWarden.CipherCreatePendingLedgerProtection.v1";

    private static ReadOnlySpan<byte> FileMagic => "WVWCPD01"u8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private readonly ICipherCreatePendingProtectionPolicy _protection;
    private readonly Action<CipherCreatePendingStoreWriteKind, string>? _beforeWrite;
    private readonly IAccountDeletionLocalWriteGuard _writeGuard;

    public DpapiCipherCreatePendingStore(IAccountDeletionLocalWriteGuard writeGuard)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "cipher-create-pending.bin"),
            CurrentUserDpapiCipherCreatePendingProtection.Instance,
            beforeWrite: null,
            writeGuard: writeGuard)
    {
    }

    internal DpapiCipherCreatePendingStore(string path)
        : this(
            path,
            CurrentUserDpapiCipherCreatePendingProtection.Instance,
            beforeWrite: null,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiCipherCreatePendingStore(
        string path,
        ICipherCreatePendingProtectionPolicy protection,
        Action<CipherCreatePendingStoreWriteKind, string>? beforeWrite = null)
        : this(
            path,
            protection,
            beforeWrite,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiCipherCreatePendingStore(
        string path,
        ICipherCreatePendingProtectionPolicy protection,
        Action<CipherCreatePendingStoreWriteKind, string>? beforeWrite,
        IAccountDeletionLocalWriteGuard writeGuard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(writeGuard);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("The pending-create store path must have a parent directory.", nameof(path));
        _lockPath = _path + ".lock";
        _protection = protection;
        _beforeWrite = beforeWrite;
        _writeGuard = writeGuard;
    }

    public CipherCreatePendingReadResult Read(string accountId, ServerEndpoints endpoints)
    {
        ValidateScope(accountId, endpoints);
        if (!_protection.IsSupported)
            return CipherCreatePendingReadResult.Unavailable;

        IDisposable? maintenanceLease = null;
        try
        {
            maintenanceLease = _writeGuard.TryAcquire(accountId, endpoints);
        }
        catch
        {
            // Reads remain available when maintenance writes cannot be authorized.
        }

        using (maintenanceLease)
        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                var ledger = ReadLedgerForMutation();
                var record = Find(ledger, accountId, endpoints);
                if (record?.IsInFlight == true && maintenanceLease is not null)
                {
                    using var recoveryLease = TryAcquireInFlightLease(record);
                    if (recoveryLease is not null)
                    {
                        record = NextRecord(record, isInFlight: false, record.Pending);
                        WriteLedger(Replace(ledger, record));
                    }
                }

                return record is null
                    ? CipherCreatePendingReadResult.Absent
                    : CipherCreatePendingReadResult.Found(record);
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return CipherCreatePendingReadResult.Corrupt;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return CipherCreatePendingReadResult.Unavailable;
            }
        }
    }

    public ICipherCreatePendingReservation? TryCreate(CipherCreatePending pending)
    {
        var normalized = Normalize(pending);
        RequireSupported();
        using var writeLease = _writeGuard.AcquireOrThrow(
            normalized.Witness.AccountId,
            normalized.Witness.Endpoints);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            if (Find(ledger, normalized.Witness.AccountId, normalized.Witness.Endpoints) is not null)
                return null;
            if (ledger.Entries.Count >= PersistedCipherCreatePendingLedger.MaximumEntries)
                throw new InvalidDataException("The pending-create ledger is full.");

            var record = new CipherCreatePendingRecord(
                Guid.NewGuid(),
                1,
                true,
                normalized);
            var reservation = AcquireInFlightLease(record);
            try
            {
                WriteLedger(PersistedCipherCreatePendingLedger.Create(
                    ledger.ToRecords().Append(record)));
                TryDeleteTemporaryFiles();
                return reservation;
            }
            catch
            {
                reservation.Dispose();
                throw;
            }
        }
    }

    public CipherCreatePendingRecord? TryUpdate(
        CipherCreatePendingRecord expected,
        CipherCreatePending pending)
    {
        var normalizedExpected = Normalize(expected);
        var normalized = Normalize(pending);
        RequireSupported();
        if (!WitnessesMatch(normalizedExpected.Pending.Witness, normalized.Witness))
            return null;
        using var writeLease = _writeGuard.AcquireOrThrow(
            normalizedExpected.Pending.Witness.AccountId,
            normalizedExpected.Pending.Witness.Endpoints);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            var entries = ledger.ToRecords().ToArray();
            var index = FindIndex(entries, normalized.Witness.AccountId, normalized.Witness.Endpoints);
            if (index < 0
                || !RecordsMatch(entries[index], normalizedExpected)
                || !WitnessesMatch(entries[index].Pending.Witness, normalized.Witness))
                return null;

            var current = entries[index];
            var merged = Merge(current.Pending, normalized);
            if (PendingStateMatches(current.Pending, merged))
                return current;

            var updated = NextRecord(current, current.IsInFlight, merged);
            entries[index] = updated;
            WriteLedger(PersistedCipherCreatePendingLedger.Create(entries));
            TryDeleteTemporaryFiles();
            return updated;
        }
    }

    public CipherCreatePendingRecord? Release(
        CipherCreatePendingRecord expected,
        CipherCreatePending pending)
    {
        var normalizedExpected = Normalize(expected);
        var normalized = Normalize(pending);
        RequireSupported();
        if (!WitnessesMatch(normalizedExpected.Pending.Witness, normalized.Witness))
            return null;
        using var writeLease = _writeGuard.AcquireOrThrow(
            normalizedExpected.Pending.Witness.AccountId,
            normalizedExpected.Pending.Witness.Endpoints);

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            var entries = ledger.ToRecords().ToArray();
            var index = FindIndex(
                entries,
                normalizedExpected.Pending.Witness.AccountId,
                normalizedExpected.Pending.Witness.Endpoints);
            if (index < 0
                || !RecordsMatch(entries[index], normalizedExpected)
                || !entries[index].IsInFlight
                || !WitnessesMatch(entries[index].Pending.Witness, normalized.Witness))
            {
                return null;
            }

            var released = NextRecord(
                entries[index],
                isInFlight: false,
                Merge(entries[index].Pending, normalized));
            entries[index] = released;
            WriteLedger(PersistedCipherCreatePendingLedger.Create(entries));
            TryDeleteTemporaryFiles();
            return released;
        }
    }

    public bool RemoveConfirmed(CipherCreatePendingRecord expected)
    {
        var normalized = Normalize(expected);
        RequireSupported();

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            var entries = ledger.ToRecords().ToArray();
            var index = FindIndex(
                entries,
                normalized.Pending.Witness.AccountId,
                normalized.Pending.Witness.Endpoints);
            if (index < 0
                || !RecordsMatch(entries[index], normalized)
                || !entries[index].Pending.CommitConfirmed
                || string.IsNullOrWhiteSpace(entries[index].Pending.ExpectedCipherId))
            {
                return false;
            }

            var remaining = entries.Where((_, candidateIndex) => candidateIndex != index);
            _beforeWrite?.Invoke(CipherCreatePendingStoreWriteKind.ConfirmedRemoval, _path);
            WriteLedger(PersistedCipherCreatePendingLedger.Create(remaining));
            TryDeleteTemporaryFiles();
            return true;
        }
    }

    public bool Abandon(CipherCreatePendingRecord expected)
    {
        var normalized = Normalize(expected);
        RequireSupported();

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            var entries = ledger.ToRecords().ToArray();
            var index = FindIndex(
                entries,
                normalized.Pending.Witness.AccountId,
                normalized.Pending.Witness.Endpoints);
            if (index < 0
                || !RecordsMatch(entries[index], normalized)
                || entries[index].IsInFlight)
            {
                return false;
            }

            using var abandonmentLease = TryAcquireInFlightLease(entries[index]);
            if (abandonmentLease is null)
                return false;

            var remaining = entries.Where((_, candidateIndex) => candidateIndex != index);
            _beforeWrite?.Invoke(CipherCreatePendingStoreWriteKind.AbandonRemoval, _path);
            WriteLedger(PersistedCipherCreatePendingLedger.Create(remaining));
            TryDeleteTemporaryFiles();
            return true;
        }
    }

    private PersistedCipherCreatePendingLedger ReadLedgerForMutation()
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
                return PersistedCipherCreatePendingLedger.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return PersistedCipherCreatePendingLedger.Empty;
            }

            if (fileBytes.Length <= FileMagic.Length
                || !fileBytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
            {
                throw new InvalidDataException("The pending-create ledger header is invalid.");
            }

            protectedBytes = fileBytes.AsSpan(FileMagic.Length).ToArray();
            entropy = CreateProtectionEntropy();
            plaintext = UnprotectOwned(protectedBytes, entropy);
            var loaded = JsonSerializer.Deserialize(
                plaintext,
                AppJsonContext.Default.PersistedCipherCreatePendingLedger);
            if (loaded is null || !loaded.TryNormalize(out var normalized))
                throw new InvalidDataException("The pending-create ledger schema is invalid.");
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

    private void WriteLedger(PersistedCipherCreatePendingLedger ledger)
    {
        if (!ledger.TryNormalize(out var normalized))
            throw new InvalidDataException("The pending-create ledger is invalid.");

        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                normalized,
                AppJsonContext.Default.PersistedCipherCreatePendingLedger);
            EnsureLength(plaintext, MaximumPlaintextLength, "Pending-create ledger plaintext");
            entropy = CreateProtectionEntropy();
            protectedBytes = ProtectOwned(plaintext, entropy);
            EnsureLength(
                protectedBytes,
                MaximumFileLength - FileMagic.Length,
                "Protected pending-create ledger");
            framedBytes = Frame(protectedBytes);
            _beforeWrite?.Invoke(CipherCreatePendingStoreWriteKind.Ledger, _path);
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
                throw new IOException("Timed out acquiring the pending-create store lock.");
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
            throw new InvalidDataException("The pending-create ledger file length is invalid.");

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("The pending-create ledger changed while being read.");
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
            throw new CryptographicException("The pending-create protector returned no payload.");
        if (ReferenceEquals(protectedBytes, plaintext)
            || ReferenceEquals(protectedBytes, entropy))
        {
            throw new CryptographicException("The pending-create protector returned an aliased payload.");
        }
        return protectedBytes;
    }

    private byte[] UnprotectOwned(byte[] protectedBytes, byte[] entropy)
    {
        var plaintext = _protection.Unprotect(protectedBytes, entropy);
        if (plaintext is null)
            throw new CryptographicException("The pending-create protector returned no plaintext.");
        var owned = ReferenceEquals(plaintext, protectedBytes)
            || ReferenceEquals(plaintext, entropy)
                ? plaintext.ToArray()
                : plaintext;
        try
        {
            EnsureLength(owned, MaximumPlaintextLength, "Pending-create ledger plaintext");
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
                        "The pending-create ledger could not be atomically replaced.",
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

    private static CipherCreatePending Normalize(CipherCreatePending pending) =>
        PersistedCipherCreatePendingEntry.FromPending(pending).ToPending();

    private static CipherCreatePendingRecord Normalize(CipherCreatePendingRecord record) =>
        PersistedCipherCreatePendingEntry.FromRecord(record).ToRecord();

    private static CipherCreatePendingRecord? Find(
        PersistedCipherCreatePendingLedger ledger,
        string accountId,
        ServerEndpoints endpoints) =>
        ledger.ToRecords().FirstOrDefault(candidate =>
            string.Equals(candidate.Pending.Witness.AccountId, accountId, StringComparison.Ordinal)
            && candidate.Pending.Witness.Endpoints == endpoints);

    private static int FindIndex(
        IReadOnlyList<CipherCreatePendingRecord> entries,
        string accountId,
        ServerEndpoints endpoints)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var witness = entries[index].Pending.Witness;
            if (string.Equals(witness.AccountId, accountId, StringComparison.Ordinal)
                && witness.Endpoints == endpoints)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool RecordsMatch(
        CipherCreatePendingRecord existing,
        CipherCreatePendingRecord expected) =>
        existing.Revision == expected.Revision
        && existing.ReservationId == expected.ReservationId
        && existing.IsInFlight == expected.IsInFlight
        && WitnessesMatch(existing.Pending.Witness, expected.Pending.Witness)
        && PendingStateMatches(existing.Pending, expected.Pending);

    private static bool WitnessesMatch(
        CipherCreateWitness existing,
        CipherCreateWitness incoming) =>
        string.Equals(existing.AccountId, incoming.AccountId, StringComparison.Ordinal)
        && existing.Endpoints == incoming.Endpoints
        && existing.CipherType == incoming.CipherType
        && string.Equals(existing.OrganizationId, incoming.OrganizationId, StringComparison.Ordinal)
        && existing.CollectionIds.SequenceEqual(incoming.CollectionIds, StringComparer.Ordinal)
        && existing.BaselineCipherIds.SequenceEqual(incoming.BaselineCipherIds, StringComparer.Ordinal)
        && string.Equals(existing.KeyFingerprint, incoming.KeyFingerprint, StringComparison.Ordinal);

    private static CipherCreatePending Merge(
        CipherCreatePending existing,
        CipherCreatePending incoming)
    {
        if (existing.ExpectedCipherId is not null
            && incoming.ExpectedCipherId is not null
            && !string.Equals(
                existing.ExpectedCipherId,
                incoming.ExpectedCipherId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The expected cipher identifier cannot be changed.");
        }

        return existing with
        {
            CommitConfirmed = existing.CommitConfirmed || incoming.CommitConfirmed,
            ExpectedCipherId = existing.ExpectedCipherId ?? incoming.ExpectedCipherId,
        };
    }

    private static bool PendingStateMatches(
        CipherCreatePending left,
        CipherCreatePending right) =>
        left.CommitConfirmed == right.CommitConfirmed
        && string.Equals(left.ExpectedCipherId, right.ExpectedCipherId, StringComparison.Ordinal);

    private static CipherCreatePendingRecord NextRecord(
        CipherCreatePendingRecord current,
        bool isInFlight,
        CipherCreatePending pending) =>
        new(
            current.ReservationId,
            checked(current.Revision + 1),
            isInFlight,
            pending);

    private static PersistedCipherCreatePendingLedger Replace(
        PersistedCipherCreatePendingLedger ledger,
        CipherCreatePendingRecord replacement)
    {
        var records = ledger.ToRecords().ToArray();
        var index = FindIndex(
            records,
            replacement.Pending.Witness.AccountId,
            replacement.Pending.Witness.Endpoints);
        if (index < 0)
            throw new InvalidDataException("The pending-create record is missing.");
        records[index] = replacement;
        return PersistedCipherCreatePendingLedger.Create(records);
    }

    private InFlightLease AcquireInFlightLease(CipherCreatePendingRecord record)
    {
        var path = GetInFlightLockPath(record.Pending.Witness);
        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
        return new InFlightLease(record, stream, path);
    }

    private InFlightLease? TryAcquireInFlightLease(CipherCreatePendingRecord record)
    {
        try
        {
            return AcquireInFlightLease(record);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string GetInFlightLockPath(CipherCreateWitness witness)
    {
        var descriptor = string.Join(
            "\n",
            witness.AccountId,
            witness.Endpoints.WebVault.Value,
            witness.Endpoints.Api.Value,
            witness.Endpoints.Identity.Value,
            witness.Endpoints.Notifications.Value,
            witness.Endpoints.Icons.Value,
            witness.Endpoints.SendWeb.Value);
        var bytes = Encoding.UTF8.GetBytes(descriptor);
        byte[]? hash = null;
        try
        {
            hash = SHA256.HashData(bytes);
            return Path.Combine(
                _directoryPath,
                $".{Path.GetFileName(_path)}.{Convert.ToHexStringLower(hash)}.inflight.lock");
        }
        finally
        {
            Clear(hash);
            Clear(bytes);
        }
    }

    private static void ValidateScope(string accountId, ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        _ = PersistedCipherCreatePendingEntry.FromPending(new CipherCreatePending(
            new CipherCreateWitness(
                accountId,
                endpoints,
                Core.Enums.CipherType.Login,
                null,
                [],
                [],
                new string('0', 64)),
            false,
            null));
    }

    private void RequireSupported()
    {
        if (!_protection.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "DPAPI pending-create storage is only supported on Windows.");
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
        or UnauthorizedAccessException;

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }

    private sealed class InFlightLease : ICipherCreatePendingReservation
    {
        private FileStream? _stream;
        private readonly string _path;

        public InFlightLease(
            CipherCreatePendingRecord record,
            FileStream stream,
            string path)
        {
            Record = record;
            _stream = stream;
            _path = path;
        }

        public CipherCreatePendingRecord Record { get; }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
                return;
            try
            {
                stream.Dispose();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                TryDeleteFile(_path);
            }
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

internal enum CipherCreatePendingStoreWriteKind
{
    Ledger,
    ConfirmedRemoval,
    AbandonRemoval,
}

internal interface ICipherCreatePendingProtectionPolicy
{
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[] entropy);

    byte[] Unprotect(byte[] protectedData, byte[] entropy);
}

internal sealed class CurrentUserDpapiCipherCreatePendingProtection
    : ICipherCreatePendingProtectionPolicy
{
    public static CurrentUserDpapiCipherCreatePendingProtection Instance { get; } = new();

    private CurrentUserDpapiCipherCreatePendingProtection()
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
