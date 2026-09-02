using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

public sealed class DpapiLoginWithDeviceQuarantineStore
    : ILoginWithDeviceQuarantineStore
{
    private const int MaximumFileLength = 1024 * 1024;
    private const int MaximumPlaintextLength = 768 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;
    private const string EntropyDomain =
        "WinVaultWarden.LoginWithDeviceQuarantineLedgerProtection.v1";

    private static ReadOnlySpan<byte> FileMagic => "WVWLDQ01"u8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directoryPath;
    private readonly string _lockPath;
    private readonly ILoginWithDeviceQuarantineProtectionPolicy _protection;
    private readonly ILoginWithDeviceQuarantineAtomicReplacePolicy _atomicReplace;
    private readonly TimeProvider _timeProvider;

    public DpapiLoginWithDeviceQuarantineStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "login-with-device-quarantine.bin"))
    {
    }

    public DpapiLoginWithDeviceQuarantineStore(string path)
        : this(
            path,
            CurrentUserDpapiLoginWithDeviceQuarantineProtection.Instance,
            WindowsWriteThroughLoginWithDeviceQuarantineAtomicReplacePolicy.Instance,
            TimeProvider.System)
    {
    }

    internal DpapiLoginWithDeviceQuarantineStore(
        string path,
        ILoginWithDeviceQuarantineProtectionPolicy protection,
        ILoginWithDeviceQuarantineAtomicReplacePolicy atomicReplace,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(atomicReplace);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException(
                "The device-login quarantine path must have a parent directory.",
                nameof(path));
        _lockPath = _path + ".lock";
        _protection = protection;
        _atomicReplace = atomicReplace;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LoginWithDeviceQuarantineStoreReadResult Read(
        LoginWithDeviceQuarantineBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateBinding(binding);
        if (!IsSupported)
            return LoginWithDeviceQuarantineStoreReadResult.Unavailable;

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                var ledger = ReadLedgerForMutation();
                var entry = ledger.Entries.FirstOrDefault(candidate =>
                    SameBinding(candidate, binding));
                return entry is null
                    ? LoginWithDeviceQuarantineStoreReadResult.Absent
                    : LoginWithDeviceQuarantineStoreReadResult.Found(entry.ToRecord());
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return LoginWithDeviceQuarantineStoreReadResult.Corrupt;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return LoginWithDeviceQuarantineStoreReadResult.Unavailable;
            }
        }
    }

    public LoginWithDeviceQuarantineRecord? TryArm(
        LoginWithDeviceQuarantineBinding binding,
        string requestId,
        DateTimeOffset armedAt,
        DateTimeOffset requestExpiresAt,
        DateTimeOffset clearEligibleAt)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateBinding(binding);
        var record = new LoginWithDeviceQuarantineRecord(
            Guid.NewGuid(),
            revision: 1,
            binding,
            requestId,
            armedAt,
            requestExpiresAt,
            clearEligibleAt);
        var entry = PersistedLoginWithDeviceQuarantineEntry.From(record);
        RequireSupported();

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            if (ledger.Entries.Any(candidate => SameBinding(candidate, binding)))
                return null;
            if (ledger.Entries.Count >= PersistedLoginWithDeviceQuarantineLedger.MaximumEntries)
            {
                throw new InvalidDataException(
                    "The device-login quarantine ledger is full.");
            }

            WriteLedger(new PersistedLoginWithDeviceQuarantineLedger(
                PersistedLoginWithDeviceQuarantineLedger.FormatName,
                PersistedLoginWithDeviceQuarantineLedger.CurrentSchemaVersion,
                ledger.Entries.Append(entry).ToArray()));
            TryDeleteTemporaryFiles();
            return record;
        }
    }

    public bool Remove(
        LoginWithDeviceQuarantineRecord expected,
        LoginWithDeviceQuarantineRemovalReason reason)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        RequireSupported();
        if (reason == LoginWithDeviceQuarantineRemovalReason.UserConfirmedAfterEligibility
            && !expected.IsClearEligible(_timeProvider.GetUtcNow()))
        {
            return false;
        }

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var ledger = ReadLedgerForMutation();
            var index = FindIndex(ledger.Entries, expected);
            if (index < 0)
                return false;

            WriteLedger(new PersistedLoginWithDeviceQuarantineLedger(
                PersistedLoginWithDeviceQuarantineLedger.FormatName,
                PersistedLoginWithDeviceQuarantineLedger.CurrentSchemaVersion,
                ledger.Entries
                    .Where((_, candidateIndex) => candidateIndex != index)
                    .ToArray()));
            TryDeleteTemporaryFiles();
            return true;
        }
    }

    private bool IsSupported => _protection.IsSupported && _atomicReplace.IsSupported;

    private PersistedLoginWithDeviceQuarantineLedger ReadLedgerForMutation()
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
                return PersistedLoginWithDeviceQuarantineLedger.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return PersistedLoginWithDeviceQuarantineLedger.Empty;
            }

            if (fileBytes.Length <= FileMagic.Length
                || !fileBytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
            {
                throw new InvalidDataException(
                    "The device-login quarantine ledger header is invalid.");
            }

            protectedBytes = fileBytes.AsSpan(FileMagic.Length).ToArray();
            entropy = CreateProtectionEntropy();
            plaintext = UnprotectOwned(
                protectedBytes,
                entropy,
                MaximumPlaintextLength,
                "Device-login quarantine ledger plaintext");
            var ledger = JsonSerializer.Deserialize(
                plaintext,
                AppJsonContext.Default.PersistedLoginWithDeviceQuarantineLedger);
            if (ledger is null || !ledger.TryValidate())
            {
                throw new InvalidDataException(
                    "The device-login quarantine ledger schema is invalid.");
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

    private void WriteLedger(PersistedLoginWithDeviceQuarantineLedger ledger)
    {
        if (!ledger.TryValidate())
            throw new InvalidDataException("The device-login quarantine ledger is invalid.");

        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                ledger,
                AppJsonContext.Default.PersistedLoginWithDeviceQuarantineLedger);
            EnsureLength(
                plaintext,
                MaximumPlaintextLength,
                "Device-login quarantine ledger plaintext");
            entropy = CreateProtectionEntropy();
            protectedBytes = ProtectOwned(plaintext, entropy);
            EnsureLength(
                protectedBytes,
                MaximumFileLength - FileMagic.Length,
                "Protected device-login quarantine ledger");
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
                    "Timed out acquiring the device-login quarantine store lock.");
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
                "The device-login quarantine ledger file length is invalid.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException(
                    "The device-login quarantine ledger changed while being read.");
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
                "The device-login quarantine protector returned no payload.");
        }
        if (ReferenceEquals(protectedBytes, plaintext)
            || ReferenceEquals(protectedBytes, entropy))
        {
            throw new CryptographicException(
                "The device-login quarantine protector returned an aliased payload.");
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
        {
            throw new CryptographicException(
                "The device-login quarantine protector returned no plaintext.");
        }
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

    private static int FindIndex(
        IReadOnlyList<PersistedLoginWithDeviceQuarantineEntry> entries,
        LoginWithDeviceQuarantineRecord expected)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.EntryId == expected.EntryId
                && entry.Revision == expected.Revision
                && SameBinding(entry, expected.Binding)
                && string.Equals(entry.RequestId, expected.RequestId, StringComparison.Ordinal)
                && entry.ArmedAt == expected.ArmedAt
                && entry.RequestExpiresAt == expected.RequestExpiresAt
                && entry.ClearEligibleAt == expected.ClearEligibleAt)
            {
                return index;
            }
        }
        return -1;
    }

    private static bool SameBinding(
        PersistedLoginWithDeviceQuarantineEntry entry,
        LoginWithDeviceQuarantineBinding binding) =>
        entry.ToEndpoints() == binding.Endpoints
        && string.Equals(
            entry.NormalizedEmail,
            binding.NormalizedEmail,
            StringComparison.Ordinal)
        && string.Equals(
            entry.DeviceIdentifier,
            binding.DeviceIdentifier,
            StringComparison.Ordinal);

    private static void ValidateBinding(LoginWithDeviceQuarantineBinding binding)
    {
        var canonical = new LoginWithDeviceQuarantineBinding(
            binding.Endpoints,
            binding.NormalizedEmail,
            binding.DeviceIdentifier);
        if (canonical != binding)
        {
            throw new ArgumentException(
                "The device-login quarantine binding is not canonical.",
                nameof(binding));
        }
    }

    private void RequireSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "DPAPI device-login quarantine storage is only supported on Windows.");
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
}

internal interface ILoginWithDeviceQuarantineProtectionPolicy
{
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[] entropy);

    byte[] Unprotect(byte[] protectedData, byte[] entropy);
}

internal sealed class CurrentUserDpapiLoginWithDeviceQuarantineProtection
    : ILoginWithDeviceQuarantineProtectionPolicy
{
    public static CurrentUserDpapiLoginWithDeviceQuarantineProtection Instance { get; } =
        new();

    private CurrentUserDpapiLoginWithDeviceQuarantineProtection()
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

internal interface ILoginWithDeviceQuarantineAtomicReplacePolicy
{
    bool IsSupported { get; }

    void Replace(string sourcePath, string destinationPath);
}

internal sealed partial class WindowsWriteThroughLoginWithDeviceQuarantineAtomicReplacePolicy
    : ILoginWithDeviceQuarantineAtomicReplacePolicy
{
    public static WindowsWriteThroughLoginWithDeviceQuarantineAtomicReplacePolicy Instance { get; }
        = new();

    private WindowsWriteThroughLoginWithDeviceQuarantineAtomicReplacePolicy()
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
                "The device-login quarantine ledger could not be atomically replaced.",
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
