using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

/// <summary>
/// Stores trusted-device keys in a canonical, account-enumerable DPAPI ledger.
/// Legacy binding-hash files remain readable and exactly removable, but are never
/// guessed during account-scope cleanup.
/// </summary>
public sealed class DpapiDeviceKeyStore : IDeviceKeyStore
{
    private const int MaximumFileLength = 512 * 1024;
    private const int MaximumPlaintextLength = 384 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;
    private const string LedgerFileName = "device-key-ledger.bin";
    private const string LedgerEntropyDomain =
        "WinVaultWarden.DeviceKeyLedgerProtection.v1";

    private static readonly string[] LedgerPropertyNames =
        ["format", "schemaVersion", "entries"];
    private static readonly string[] EntryPropertyNames =
    [
        "accountId",
        "webVault",
        "api",
        "identity",
        "notifications",
        "icons",
        "sendWeb",
        "deviceIdentifier",
        "protectedDeviceKey",
    ];

    private static ReadOnlySpan<byte> FileMagic => "WVWDKL01"u8;

    private readonly object _gate = new();
    private readonly string _directoryPath;
    private readonly string _ledgerPath;
    private readonly string _lockPath;
    private readonly IDeviceKeyProtectionPolicy _protection;
    private readonly IDeviceKeyAtomicReplacePolicy _atomicReplace;
    private readonly IAccountDeletionLocalWriteGuard _localWriteGuard;

    public DpapiDeviceKeyStore(IAccountDeletionLocalWriteGuard localWriteGuard)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "device-keys"),
            CurrentUserDpapiDeviceKeyProtection.Instance,
            WindowsWriteThroughDeviceKeyAtomicReplacePolicy.Instance,
            localWriteGuard)
    {
    }

    public DpapiDeviceKeyStore(string directoryPath)
        : this(
            directoryPath,
            CurrentUserDpapiDeviceKeyProtection.Instance,
            WindowsWriteThroughDeviceKeyAtomicReplacePolicy.Instance,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiDeviceKeyStore(
        string directoryPath,
        IDeviceKeyProtectionPolicy protection)
        : this(
            directoryPath,
            protection,
            FileMoveDeviceKeyAtomicReplacePolicy.Instance,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiDeviceKeyStore(
        string directoryPath,
        IDeviceKeyProtectionPolicy protection,
        IDeviceKeyAtomicReplacePolicy atomicReplace)
        : this(
            directoryPath,
            protection,
            atomicReplace,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiDeviceKeyStore(
        string directoryPath,
        IDeviceKeyProtectionPolicy protection,
        IAccountDeletionLocalWriteGuard localWriteGuard)
        : this(
            directoryPath,
            protection,
            FileMoveDeviceKeyAtomicReplacePolicy.Instance,
            localWriteGuard)
    {
    }

    internal DpapiDeviceKeyStore(
        string directoryPath,
        IDeviceKeyProtectionPolicy protection,
        IDeviceKeyAtomicReplacePolicy atomicReplace,
        IAccountDeletionLocalWriteGuard localWriteGuard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(atomicReplace);
        ArgumentNullException.ThrowIfNull(localWriteGuard);
        _directoryPath = Path.GetFullPath(directoryPath);
        _ledgerPath = Path.Combine(_directoryPath, LedgerFileName);
        _lockPath = _ledgerPath + ".lock";
        _protection = protection;
        _atomicReplace = atomicReplace;
        _localWriteGuard = localWriteGuard;
    }

    public bool TryLoad(DeviceKeyBinding binding, out byte[] deviceKey)
    {
        ValidateBinding(binding);
        deviceKey = [];
        if (!IsSupported)
            return false;

        IDisposable? maintenanceLease = null;
        try
        {
            maintenanceLease = _localWriteGuard.TryAcquire(
                binding.AccountId,
                binding.Endpoints);
        }
        catch
        {
            // A maintenance lease is optional: reads remain available but must not mutate storage.
        }

        using (maintenanceLease)
        lock (_gate)
        {
            PersistedDeviceKeyLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                ledger = ReadLedgerForMutation();
                var entry = ledger.Entries.FirstOrDefault(candidate =>
                    SameBinding(candidate, binding));
                if (entry is not null)
                {
                    var loaded = TryUnprotectEntry(entry, binding, out deviceKey);
                    if (loaded && maintenanceLease is not null)
                        DeleteLegacyAfterCanonicalLoadBestEffort(binding);
                    return loaded;
                }

                if (!TryLoadLegacy(binding, out deviceKey))
                    return false;
                if (maintenanceLease is not null)
                    MigrateLegacyAfterSuccessfulLoadBestEffort(binding, deviceKey, ledger);
                return true;
            }
            catch (Exception exception) when (IsReadableFailure(exception))
            {
                Clear(deviceKey);
                deviceKey = [];
                return false;
            }
            finally
            {
                ledger?.ClearSecrets();
            }
        }
    }

    public void Save(DeviceKeyBinding binding, ReadOnlySpan<byte> deviceKey)
    {
        ValidateBinding(binding);
        if (deviceKey.Length != IDeviceKeyStore.KeyLength)
        {
            throw new ArgumentException(
                $"Device key must be exactly {IDeviceKeyStore.KeyLength} bytes.",
                nameof(deviceKey));
        }
        RequireSupported();
        using var writeLease = _localWriteGuard.AcquireOrThrow(
            binding.AccountId,
            binding.Endpoints);

        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedDeviceKey = null;
        PersistedDeviceKeyEntry? replacement = null;
        try
        {
            plaintext = deviceKey.ToArray();
            entropy = binding.CreateProtectionEntropy();
            protectedDeviceKey = ProtectOwned(plaintext, entropy, "device key");
            EnsureLength(
                protectedDeviceKey,
                PersistedDeviceKeyEntry.MaximumProtectedDeviceKeyLength,
                "Protected device key");
            replacement = PersistedDeviceKeyEntry.Create(binding, protectedDeviceKey);

            lock (_gate)
            {
                PersistedDeviceKeyLedger? ledger = null;
                try
                {
                    using var storeLock = AcquireStoreLock();
                    DeleteCanonicalTemporaryFilesStrict();
                    ledger = ReadLedgerForMutation();
                    var entries = ledger.Entries.ToArray();
                    var index = FindIndex(entries, binding);
                    if (index < 0)
                    {
                        if (entries.Length >= PersistedDeviceKeyLedger.MaximumEntries)
                            throw new InvalidDataException("The device-key ledger is full.");
                        entries = entries.Append(replacement).ToArray();
                    }
                    else
                    {
                        entries[index] = replacement;
                    }

                    WriteLedger(new PersistedDeviceKeyLedger(
                        PersistedDeviceKeyLedger.FormatName,
                        PersistedDeviceKeyLedger.CurrentSchemaVersion,
                        entries));
                    DeleteLegacyExactAfterCanonicalCommit(binding);
                }
                finally
                {
                    ledger?.ClearSecrets();
                }
            }
        }
        finally
        {
            replacement?.ClearSecret();
            Clear(protectedDeviceKey);
            Clear(entropy);
            Clear(plaintext);
        }
    }

    public bool Remove(DeviceKeyBinding binding) =>
        RemoveExact(binding) == DeviceKeyRemovalStatus.Removed;

    public DeviceKeyRemovalStatus RemoveExact(DeviceKeyBinding binding)
    {
        ValidateBinding(binding);
        if (!IsSupported)
            return DeviceKeyRemovalStatus.Unavailable;

        lock (_gate)
        {
            PersistedDeviceKeyLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                DeleteCanonicalTemporaryFilesStrict();
                ledger = ReadLedgerForMutation();
                var entries = ledger.Entries.ToArray();
                var index = FindIndex(entries, binding);
                var removedCanonical = index >= 0;
                if (removedCanonical)
                {
                    WriteLedger(new PersistedDeviceKeyLedger(
                        PersistedDeviceKeyLedger.FormatName,
                        PersistedDeviceKeyLedger.CurrentSchemaVersion,
                        entries.Where((_, candidateIndex) => candidateIndex != index).ToArray()));
                }

                var legacyStatus = DeleteLegacyExact(binding);
                if (legacyStatus == DeviceKeyRemovalStatus.Unavailable)
                    return DeviceKeyRemovalStatus.Unavailable;
                return removedCanonical || legacyStatus == DeviceKeyRemovalStatus.Removed
                    ? DeviceKeyRemovalStatus.Removed
                    : DeviceKeyRemovalStatus.Absent;
            }
            catch (Exception exception) when (IsCorruptFailure(exception))
            {
                return DeviceKeyRemovalStatus.Unavailable;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception))
            {
                return DeviceKeyRemovalStatus.Unavailable;
            }
            finally
            {
                ledger?.ClearSecrets();
            }
        }
    }

    public AccountScopedRemovalStatus RemoveAccountScope(
        string accountId,
        ServerEndpoints endpoints)
    {
        ValidateAccountScope(accountId, endpoints);
        if (!IsSupported)
            return AccountScopedRemovalStatus.Unavailable;

        lock (_gate)
        {
            PersistedDeviceKeyLedger? ledger = null;
            try
            {
                using var storeLock = AcquireStoreLock();
                DeleteCanonicalTemporaryFilesStrict();
                if (HasUntrackedArtifacts())
                    return AccountScopedRemovalStatus.Unavailable;

                ledger = ReadLedgerForMutation();
                ValidateEntrySecrets(ledger.Entries);
                var matching = ledger.Entries
                    .Where(entry => SameAccountScope(entry, accountId, endpoints))
                    .ToArray();
                if (matching.Length == 0)
                    return AccountScopedRemovalStatus.Absent;

                var remaining = ledger.Entries
                    .Where(entry => !SameAccountScope(entry, accountId, endpoints))
                    .ToArray();
                try
                {
                    WriteLedger(new PersistedDeviceKeyLedger(
                        PersistedDeviceKeyLedger.FormatName,
                        PersistedDeviceKeyLedger.CurrentSchemaVersion,
                        remaining));
                }
                catch (CryptographicException exception)
                {
                    throw new IOException(
                        "The device-key ledger could not be protected for account removal.",
                        exception);
                }
                DeleteCanonicalTemporaryFilesStrict();
                if (HasUntrackedArtifacts())
                    return AccountScopedRemovalStatus.Unavailable;
                return AccountScopedRemovalStatus.Removed;
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

    private bool IsSupported => _protection.IsSupported && _atomicReplace.IsSupported;

    private PersistedDeviceKeyLedger ReadLedgerForMutation()
    {
        byte[]? fileBytes = null;
        byte[]? protectedBytes = null;
        byte[]? entropy = null;
        byte[]? plaintext = null;
        try
        {
            try
            {
                fileBytes = ReadBounded(_ledgerPath, MaximumFileLength);
            }
            catch (FileNotFoundException)
            {
                return PersistedDeviceKeyLedger.Empty;
            }
            catch (DirectoryNotFoundException)
            {
                return PersistedDeviceKeyLedger.Empty;
            }

            if (fileBytes.Length <= FileMagic.Length
                || !fileBytes.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
            {
                throw new InvalidDataException("The device-key ledger header is invalid.");
            }

            protectedBytes = fileBytes.AsSpan(FileMagic.Length).ToArray();
            entropy = CreateLedgerEntropy();
            plaintext = UnprotectOwned(
                protectedBytes,
                entropy,
                MaximumPlaintextLength,
                "device-key ledger");
            ValidateJsonShape(plaintext);
            var loaded = JsonSerializer.Deserialize(
                plaintext,
                AppJsonContext.Default.PersistedDeviceKeyLedger);
            if (loaded is null || !loaded.TryNormalize(out var normalized))
            {
                loaded?.ClearSecrets();
                throw new InvalidDataException("The device-key ledger schema is invalid.");
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
            || entries.GetArrayLength() > PersistedDeviceKeyLedger.MaximumEntries)
        {
            throw new InvalidDataException("The device-key entry collection is invalid.");
        }
        foreach (var entry in entries.EnumerateArray())
            RequireExactProperties(entry, EntryPropertyNames);
    }

    private static void RequireExactProperties(
        JsonElement element,
        IReadOnlyCollection<string> expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A device-key JSON object was expected.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedNames.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new InvalidDataException(
                    "The device-key JSON schema is not canonical.");
            }
        }
        if (seen.Count != expectedNames.Count)
            throw new InvalidDataException("The device-key JSON schema is incomplete.");
    }

    private void WriteLedger(PersistedDeviceKeyLedger ledger)
    {
        if (!ledger.TryNormalize(out var normalized))
            throw new InvalidDataException("The device-key ledger is invalid.");

        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                normalized,
                AppJsonContext.Default.PersistedDeviceKeyLedger);
            EnsureLength(plaintext, MaximumPlaintextLength, "Device-key ledger plaintext");
            entropy = CreateLedgerEntropy();
            protectedBytes = ProtectOwned(plaintext, entropy, "device-key ledger");
            EnsureLength(
                protectedBytes,
                MaximumFileLength - FileMagic.Length,
                "Protected device-key ledger");
            framedBytes = Frame(protectedBytes);
            WriteAtomic(_ledgerPath, framedBytes);
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
                throw new IOException("Timed out acquiring the device-key ledger lock.");
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
            throw new InvalidDataException("The device-key file length is invalid.");

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("The device-key file changed while being read.");
            return bytes;
        }
        catch
        {
            Clear(bytes);
            throw;
        }
    }

    private bool TryUnprotectEntry(
        PersistedDeviceKeyEntry entry,
        DeviceKeyBinding binding,
        out byte[] deviceKey)
    {
        deviceKey = [];
        byte[]? protectedBytes = null;
        byte[]? entropy = null;
        byte[]? plaintext = null;
        try
        {
            protectedBytes = entry.ProtectedDeviceKey.ToArray();
            entropy = binding.CreateProtectionEntropy();
            plaintext = UnprotectOwned(
                protectedBytes,
                entropy,
                IDeviceKeyStore.KeyLength,
                "device key");
            if (plaintext.Length != IDeviceKeyStore.KeyLength)
                throw new InvalidDataException("The device key has an invalid length.");
            deviceKey = plaintext;
            plaintext = null;
            return true;
        }
        finally
        {
            Clear(plaintext);
            Clear(entropy);
            Clear(protectedBytes);
        }
    }

    private void ValidateEntrySecrets(IEnumerable<PersistedDeviceKeyEntry> entries)
    {
        foreach (var entry in entries)
        {
            byte[]? deviceKey = null;
            try
            {
                if (!TryUnprotectEntry(entry, entry.ToBinding(), out deviceKey))
                    throw new InvalidDataException("A device-key ledger entry could not be opened.");
            }
            finally
            {
                Clear(deviceKey);
            }
        }
    }

    private bool TryLoadLegacy(DeviceKeyBinding binding, out byte[] deviceKey)
    {
        deviceKey = [];
        byte[]? protectedBytes = null;
        byte[]? entropy = null;
        byte[]? plaintext = null;
        try
        {
            try
            {
                protectedBytes = ReadBounded(
                    GetLegacyPath(binding),
                    PersistedDeviceKeyEntry.MaximumProtectedDeviceKeyLength);
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }

            entropy = binding.CreateProtectionEntropy();
            plaintext = UnprotectOwned(
                protectedBytes,
                entropy,
                IDeviceKeyStore.KeyLength,
                "legacy device key");
            if (plaintext.Length != IDeviceKeyStore.KeyLength)
                throw new InvalidDataException("The legacy device key has an invalid length.");
            deviceKey = plaintext;
            plaintext = null;
            return true;
        }
        finally
        {
            Clear(plaintext);
            Clear(entropy);
            Clear(protectedBytes);
        }
    }

    private void MigrateLegacyAfterSuccessfulLoadBestEffort(
        DeviceKeyBinding binding,
        byte[] deviceKey,
        PersistedDeviceKeyLedger ledger)
    {
        try
        {
            MigrateLegacyAfterSuccessfulLoad(binding, deviceKey, ledger);
        }
        catch (Exception exception) when (IsReadableFailure(exception))
        {
            // The exact legacy key was already authenticated. Migration is maintenance only;
            // account-scope removal remains fail-closed while the legacy artifact exists.
        }
    }

    private void MigrateLegacyAfterSuccessfulLoad(
        DeviceKeyBinding binding,
        byte[] deviceKey,
        PersistedDeviceKeyLedger ledger)
    {
        byte[]? plaintext = null;
        byte[]? entropy = null;
        byte[]? protectedDeviceKey = null;
        PersistedDeviceKeyEntry? migrated = null;
        try
        {
            DeleteCanonicalTemporaryFilesStrict();
            if (ledger.Entries.Count >= PersistedDeviceKeyLedger.MaximumEntries)
                throw new InvalidDataException("The device-key ledger is full.");

            plaintext = deviceKey.ToArray();
            entropy = binding.CreateProtectionEntropy();
            protectedDeviceKey = ProtectOwned(plaintext, entropy, "legacy device key");
            EnsureLength(
                protectedDeviceKey,
                PersistedDeviceKeyEntry.MaximumProtectedDeviceKeyLength,
                "Protected migrated device key");
            migrated = PersistedDeviceKeyEntry.Create(binding, protectedDeviceKey);
            WriteLedger(new PersistedDeviceKeyLedger(
                PersistedDeviceKeyLedger.FormatName,
                PersistedDeviceKeyLedger.CurrentSchemaVersion,
                ledger.Entries.Append(migrated).ToArray()));
            DeleteLegacyExactAfterCanonicalCommit(binding);
        }
        finally
        {
            migrated?.ClearSecret();
            Clear(protectedDeviceKey);
            Clear(entropy);
            Clear(plaintext);
        }
    }

    private void DeleteLegacyAfterCanonicalLoadBestEffort(DeviceKeyBinding binding)
    {
        try
        {
            DeleteLegacyExactAfterCanonicalCommit(binding);
        }
        catch (Exception exception) when (IsUnavailableFailure(exception))
        {
            // Keep serving the authenticated canonical entry. Scope removal will continue to
            // report Unavailable until a later exact load can delete the stale legacy file.
        }
    }

    private DeviceKeyRemovalStatus DeleteLegacyExact(DeviceKeyBinding binding)
    {
        var path = GetLegacyPath(binding);
        try
        {
            using (new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None))
            {
            }
            File.Delete(path);
            return DeviceKeyRemovalStatus.Removed;
        }
        catch (FileNotFoundException)
        {
            return DeviceKeyRemovalStatus.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return DeviceKeyRemovalStatus.Absent;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DeviceKeyRemovalStatus.Unavailable;
        }
    }

    private void DeleteLegacyExactAfterCanonicalCommit(DeviceKeyBinding binding)
    {
        if (DeleteLegacyExact(binding) == DeviceKeyRemovalStatus.Unavailable)
        {
            throw new IOException(
                "The legacy device-key file could not be removed after migration.");
        }
    }

    private bool HasUntrackedArtifacts()
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(_directoryPath))
        {
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(fullPath, _ledgerPath, StringComparison.Ordinal)
                && !string.Equals(fullPath, _lockPath, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void DeleteCanonicalTemporaryFilesStrict()
    {
        foreach (var path in Directory.EnumerateFiles(
            _directoryPath,
            $".{LedgerFileName}.*.tmp"))
        {
            File.Delete(path);
        }
        if (Directory.EnumerateFiles(
            _directoryPath,
            $".{LedgerFileName}.*.tmp").Any())
        {
            throw new IOException("A device-key ledger temporary file could not be removed.");
        }
    }

    private string GetLegacyPath(DeviceKeyBinding binding) =>
        Path.Combine(_directoryPath, binding.CreateStorageFileName());

    private static byte[] CreateLedgerEntropy()
    {
        var domain = Encoding.UTF8.GetBytes(LedgerEntropyDomain);
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

    private byte[] ProtectOwned(byte[] plaintext, byte[] entropy, string description)
    {
        var protectedBytes = _protection.Protect(plaintext, entropy);
        if (protectedBytes is null
            || ReferenceEquals(protectedBytes, plaintext)
            || ReferenceEquals(protectedBytes, entropy))
        {
            throw new CryptographicException(
                $"The {description} protector returned an invalid aliased payload.");
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
            throw new CryptographicException($"The {description} protector returned no plaintext.");
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

    private void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(_directoryPath);
        var temporaryPath = Path.Combine(
            _directoryPath,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
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

            _atomicReplace.Replace(temporaryPath, path);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
                File.Delete(temporaryPath);
        }
    }

    private static int FindIndex(
        IReadOnlyList<PersistedDeviceKeyEntry> entries,
        DeviceKeyBinding binding)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            if (SameBinding(entries[index], binding))
                return index;
        }
        return -1;
    }

    private static bool SameBinding(
        PersistedDeviceKeyEntry entry,
        DeviceKeyBinding binding) =>
        string.Equals(entry.AccountId, binding.AccountId, StringComparison.Ordinal)
        && entry.ToEndpoints() == binding.Endpoints
        && string.Equals(
            entry.DeviceIdentifier,
            binding.DeviceIdentifier,
            StringComparison.Ordinal);

    private static bool SameAccountScope(
        PersistedDeviceKeyEntry entry,
        string accountId,
        ServerEndpoints endpoints) =>
        string.Equals(entry.AccountId, accountId, StringComparison.Ordinal)
        && entry.ToEndpoints() == endpoints;

    private static void ValidateBinding(DeviceKeyBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var canonical = new DeviceKeyBinding(
            binding.AccountId,
            binding.Endpoints,
            binding.DeviceIdentifier);
        if (!string.Equals(canonical.AccountId, binding.AccountId, StringComparison.Ordinal)
            || canonical.Endpoints != binding.Endpoints
            || !string.Equals(
                canonical.DeviceIdentifier,
                binding.DeviceIdentifier,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The device-key binding is not canonical.", nameof(binding));
        }
    }

    private static void ValidateAccountScope(string accountId, ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        _ = new DeviceKeyBinding(accountId, endpoints, "account-scope-validation");
        if (new[]
            {
                endpoints.WebVault.Value,
                endpoints.Api.Value,
                endpoints.Identity.Value,
                endpoints.Notifications.Value,
                endpoints.Icons.Value,
                endpoints.SendWeb.Value,
            }.Any(value => value.Length > PersistedDeviceKeyEntry.MaximumEndpointLength))
        {
            throw new ArgumentException(
                "Each account-scope endpoint must not exceed 2048 characters.",
                nameof(endpoints));
        }
    }

    private void RequireSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "DPAPI device-key storage is only supported on Windows.");
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

    private static bool IsReadableFailure(Exception exception) =>
        IsCorruptFailure(exception) || IsUnavailableFailure(exception);

    private static void Clear(byte[]? buffer)
    {
        if (buffer is not null)
            CryptographicOperations.ZeroMemory(buffer);
    }
}

internal interface IDeviceKeyProtectionPolicy
{
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[] entropy);

    byte[] Unprotect(byte[] protectedData, byte[] entropy);
}

internal sealed class CurrentUserDpapiDeviceKeyProtection : IDeviceKeyProtectionPolicy
{
    public static CurrentUserDpapiDeviceKeyProtection Instance { get; } = new();

    private CurrentUserDpapiDeviceKeyProtection()
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

internal interface IDeviceKeyAtomicReplacePolicy
{
    bool IsSupported { get; }

    void Replace(string sourcePath, string destinationPath);
}

internal sealed class FileMoveDeviceKeyAtomicReplacePolicy
    : IDeviceKeyAtomicReplacePolicy
{
    public static FileMoveDeviceKeyAtomicReplacePolicy Instance { get; } = new();

    private FileMoveDeviceKeyAtomicReplacePolicy()
    {
    }

    public bool IsSupported => true;

    public void Replace(string sourcePath, string destinationPath) =>
        File.Move(sourcePath, destinationPath, overwrite: true);
}

internal sealed partial class WindowsWriteThroughDeviceKeyAtomicReplacePolicy
    : IDeviceKeyAtomicReplacePolicy
{
    public static WindowsWriteThroughDeviceKeyAtomicReplacePolicy Instance { get; } = new();

    private WindowsWriteThroughDeviceKeyAtomicReplacePolicy()
    {
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Replace(string sourcePath, string destinationPath)
    {
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
                "The device-key ledger could not be atomically replaced.",
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
