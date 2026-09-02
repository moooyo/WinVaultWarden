using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

public sealed class DpapiTokenStore : ITokenStore
{
    private const int MaximumIndexFileLength = 128 * 1024;
    private const int MaximumBlobFileLength = 512 * 1024;
    private const int MaximumLegacyFileLength = 2 * 1024 * 1024;
    private const int MaximumLegacyPlaintextLength = 2 * 1024 * 1024;
    private const int MaximumIndexPlaintextLength = 96 * 1024;
    private const int MaximumBlobPlaintextLength = 384 * 1024;
    private const int StoreLockAttempts = 100;
    private const int StoreLockRetryMilliseconds = 10;
    private const string IndexEntropyDomain = "WinVaultWarden.SessionIndexProtection.v1";

    private static ReadOnlySpan<byte> IndexMagic => "WVWIDX01"u8;
    private static ReadOnlySpan<byte> BlobMagic => "WVWBLOB1"u8;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _directoryPath;
    private readonly string _blobDirectoryPath;
    private readonly string _lockPath;
    private readonly ITokenStoreProtectionPolicy _protection;
    private readonly IAccountDeletionLocalWriteGuard _writeGuard;
    private readonly Action<PersistedSession>? _migrationWriter;
    private readonly Func<string, byte[]>? _reader;
    private readonly Action<string>? _deleteFile;
    private readonly Action<TokenStoreWriteKind, string>? _beforeWrite;

    public DpapiTokenStore(IAccountDeletionLocalWriteGuard writeGuard)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "session.bin"),
            CurrentUserDpapiTokenStoreProtection.Instance,
            writeGuard)
    {
    }

    public DpapiTokenStore(string path)
        : this(
            path,
            CurrentUserDpapiTokenStoreProtection.Instance,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
    }

    internal DpapiTokenStore(
        string path,
        Action<PersistedSession> migrationWriter)
        : this(
            path,
            CurrentUserDpapiTokenStoreProtection.Instance,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
        ArgumentNullException.ThrowIfNull(migrationWriter);
        _migrationWriter = migrationWriter;
    }

    internal DpapiTokenStore(
        string path,
        Func<string, byte[]> reader)
        : this(
            path,
            CurrentUserDpapiTokenStoreProtection.Instance,
            AllowAllAccountDeletionLocalWriteGuard.Instance)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    internal DpapiTokenStore(
        string path,
        ITokenStoreProtectionPolicy protection,
        Action<TokenStoreWriteKind, string>? beforeWrite = null,
        Func<string, byte[]>? reader = null,
        Action<string>? deleteFile = null)
        : this(
            path,
            protection,
            AllowAllAccountDeletionLocalWriteGuard.Instance,
            beforeWrite,
            reader,
            deleteFile)
    {
    }

    internal DpapiTokenStore(
        string path,
        ITokenStoreProtectionPolicy protection,
        IAccountDeletionLocalWriteGuard writeGuard,
        Action<TokenStoreWriteKind, string>? beforeWrite = null,
        Func<string, byte[]>? reader = null,
        Action<string>? deleteFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(writeGuard);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("Token store path must have a parent directory.", nameof(path));
        _blobDirectoryPath = _path + ".d";
        _lockPath = _path + ".lock";
        _protection = protection;
        _writeGuard = writeGuard;
        _beforeWrite = beforeWrite;
        _reader = reader;
        _deleteFile = deleteFile;
    }

    internal string BlobDirectoryPath => _blobDirectoryPath;

    public bool TryLoad(out PersistedSession session)
    {
        session = null!;
        if (!_protection.IsSupported)
            return false;

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                if (!TryReadState(out var index, out var legacy))
                    return false;

                if (legacy is not null)
                {
                    session = legacy.Sessions[0];
                    return true;
                }

                index = TryCleanupAfterCommit(index!);
                var active = index.Accounts.FirstOrDefault(account =>
                    string.Equals(account.BindingId, index.ActiveBindingId, StringComparison.Ordinal));
                return active is not null && TryReadSessionBlob(active, out session);
            }
            catch (Exception exception) when (IsReadableFailure(exception))
            {
                session = null!;
                return false;
            }
        }
    }

    public bool TryLoad(
        string accountId,
        ServerEndpoints endpoints,
        out PersistedSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        session = null!;
        if (!_protection.IsSupported)
            return false;

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                if (!TryReadState(out var index, out var legacy))
                    return false;

                if (legacy is not null)
                {
                    var loaded = legacy.Sessions.FirstOrDefault(candidate =>
                        candidate.AccountId == accountId
                        && candidate.Endpoints == endpoints);
                    if (loaded is null)
                        return false;
                    session = loaded;
                    return true;
                }

                index = TryCleanupAfterCommit(index!);
                var bindingId = new PersistedAccountBinding(
                    accountId,
                    endpoints).CreateIdentifier();
                var account = index.Accounts.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.BindingId,
                        bindingId,
                        StringComparison.Ordinal));
                return account is not null
                    && TryReadSessionBlob(account, out session);
            }
            catch (Exception exception) when (IsReadableFailure(exception))
            {
                session = null!;
                return false;
            }
        }
    }

    public IReadOnlyList<PersistedAccountMetadata> GetSavedAccounts()
    {
        if (!_protection.IsSupported)
            return [];

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                if (!TryReadState(out var index, out var legacy))
                    return [];

                if (legacy is not null)
                {
                    return legacy.Sessions
                        .Select((session, position) =>
                            PersistedAccountMetadata.FromSession(session, position == 0))
                        .ToArray();
                }

                index = TryCleanupAfterCommit(index!);
                return index.Accounts
                    .Select(account => account.ToMetadata(string.Equals(
                        account.BindingId,
                        index.ActiveBindingId,
                        StringComparison.Ordinal)))
                    .ToArray();
            }
            catch (Exception exception) when (IsReadableFailure(exception))
            {
                return [];
            }
        }
    }

    public void Save(PersistedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_protection.IsSupported)
            throw new PlatformNotSupportedException("DPAPI token storage is only supported on Windows.");
        if (!session.TryMigrate(out var normalized))
            throw new InvalidDataException("Persisted session binding is invalid.");

        using var writeLease = _writeGuard.AcquireOrThrow(
            normalized.AccountId,
            normalized.Endpoints!);
        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            var index = ReadIndexForMutation();
            var binding = new PersistedAccountBinding(normalized.AccountId, normalized.Endpoints!);
            var bindingId = binding.CreateIdentifier();
            var revision = Guid.NewGuid();
            var entry = new PersistedSessionIndexEntry(
                bindingId,
                normalized.AccountId,
                normalized.Email,
                normalized.Endpoints!,
                revision);
            if (!entry.TryNormalize(out entry))
                throw new InvalidDataException("Persisted session metadata is invalid.");

            var blobPath = GetBlobPath(binding, revision);
            WriteSessionBlob(blobPath, entry, normalized);
            var indexCommitted = false;
            try
            {
                var previous = index.Accounts.FirstOrDefault(account =>
                    string.Equals(account.BindingId, bindingId, StringComparison.Ordinal));
                var remaining = index.Accounts
                    .Where(account => !string.Equals(account.BindingId, bindingId, StringComparison.Ordinal))
                    .ToArray();
                var accounts = new[] { entry }
                    .Concat(remaining)
                    .Take(PersistedSessionCollection.MaximumSessions)
                    .ToArray();
                var removed = new List<(string BindingId, Guid Revision)>();
                if (previous is not null)
                    removed.Add((previous.BindingId, previous.BlobRevision));
                removed.AddRange(remaining
                    .Skip(PersistedSessionCollection.MaximumSessions - 1)
                    .Select(account => (account.BindingId, account.BlobRevision)));

                var updated = PersistedSessionIndex.Create(
                    accounts,
                    MergePendingDeletes(index.PendingDeletes, removed));
                WriteIndex(updated);
                indexCommitted = true;
                TryCleanupAfterCommit(updated);
            }
            finally
            {
                if (!indexCommitted)
                    TryDeleteFile(blobPath);
            }
        }
    }

    public bool TryActivate(
        string accountId,
        ServerEndpoints endpoints,
        out PersistedSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        session = null!;
        if (!_protection.IsSupported)
            return false;

        using var writeLease = _writeGuard.AcquireOrThrow(accountId, endpoints);
        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                var index = ReadIndexForMutation();
                var bindingId = new PersistedAccountBinding(accountId, endpoints).CreateIdentifier();
                var position = index.Accounts.ToList().FindIndex(account =>
                    string.Equals(account.BindingId, bindingId, StringComparison.Ordinal));
                if (position < 0
                    || !TryReadSessionBlob(index.Accounts[position], out var loaded))
                    return false;

                if (position > 0)
                {
                    var reordered = new[] { index.Accounts[position] }
                        .Concat(index.Accounts.Where((_, candidatePosition) =>
                            candidatePosition != position));
                    WriteIndex(PersistedSessionIndex.Create(reordered, index.PendingDeletes));
                }

                session = loaded;
                return true;
            }
            catch (Exception exception) when (IsReadableFailure(exception))
            {
                session = null!;
                return false;
            }
        }
    }

    public bool Remove(string accountId, ServerEndpoints endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!_protection.IsSupported)
            return false;

        lock (_gate)
        {
            try
            {
                using var storeLock = AcquireStoreLock();
                var index = ReadIndexForMutation(cleanupPending: false);
                var bindingId = new PersistedAccountBinding(accountId, endpoints).CreateIdentifier();
                var account = index.Accounts.FirstOrDefault(candidate =>
                    string.Equals(candidate.BindingId, bindingId, StringComparison.Ordinal));
                var pendingForBinding = index.PendingDeletes.FirstOrDefault(candidate =>
                    string.Equals(candidate.BindingId, bindingId, StringComparison.Ordinal));
                var pendingRevisions = EnumerateBlobRevisions(bindingId).ToList();
                var temporaryPaths = EnumerateBlobTemporaryPaths(bindingId);
                if (account is null
                    && pendingForBinding is null
                    && pendingRevisions.Count == 0)
                {
                    return TryDeleteBlobTemporaryFiles(temporaryPaths);
                }
                if (account is not null)
                    pendingRevisions.Add(account.BlobRevision);
                if (pendingForBinding is not null)
                    pendingRevisions.AddRange(pendingForBinding.BlobRevisions);

                var pending = MergePendingDeletes(
                    index.PendingDeletes.Where(candidate =>
                        !string.Equals(candidate.BindingId, bindingId, StringComparison.Ordinal)),
                    pendingRevisions.Distinct().Select(revision => (bindingId, revision)));
                var staged = PersistedSessionIndex.Create(
                    index.Accounts.Where(candidate =>
                        !string.Equals(candidate.BindingId, bindingId, StringComparison.Ordinal)),
                    pending);
                WriteIndex(staged);

                var cleaned = CleanupPendingDeletes(staged, persistChanges: true);
                var temporaryFilesDeleted = TryDeleteBlobTemporaryFiles(temporaryPaths);
                return temporaryFilesDeleted
                    && !cleaned.PendingDeletes.Any(candidate =>
                        string.Equals(candidate.BindingId, bindingId, StringComparison.Ordinal));
            }
            catch (Exception exception) when (IsReadableFailure(exception))
            {
                return false;
            }
        }
    }

    public void Clear()
    {
        if (!_protection.IsSupported)
            throw new PlatformNotSupportedException("DPAPI token storage is only supported on Windows.");

        lock (_gate)
        {
            using var storeLock = AcquireStoreLock();
            WriteIndex(PersistedSessionIndex.Empty);
            Exception? deletionFailure = null;
            try
            {
                foreach (var file in Directory.EnumerateFiles(_blobDirectoryPath))
                {
                    var fileName = Path.GetFileName(file);
                    if (!IsOwnedBlobFileName(fileName)
                        && !IsOwnedBlobTemporaryFileName(fileName))
                    {
                        continue;
                    }
                    try
                    {
                        _beforeWrite?.Invoke(TokenStoreWriteKind.SessionBlobDelete, file);
                        DeleteFile(file);
                    }
                    catch (FileNotFoundException)
                    {
                    }
                    catch (DirectoryNotFoundException)
                    {
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        deletionFailure ??= exception;
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                deletionFailure ??= exception;
            }

            if (deletionFailure is not null)
                throw deletionFailure;

            DeleteFileIfPresent(_path);
            if (!TryDeleteIndexTemporaryFiles())
                throw new IOException("One or more persisted session temporary files could not be deleted.");
            TryDeleteBlobDirectory();
        }
    }

    private PersistedSessionIndex ReadIndexForMutation(bool cleanupPending = true)
    {
        if (!TryReadState(out var index, out var legacy))
            return PersistedSessionIndex.Empty;
        return legacy is null
            ? cleanupPending
                ? TryCleanupAfterCommit(index!)
                : index!
            : MigrateLegacy(legacy);
    }

    private void TryMigrateLegacyBestEffort(PersistedSessionCollection legacy)
    {
        try
        {
            MigrateLegacy(legacy);
        }
        catch (Exception exception) when (IsReadableFailure(exception))
        {
        }
    }

    private PersistedSessionIndex MigrateLegacy(PersistedSessionCollection legacy)
    {
        _migrationWriter?.Invoke(legacy.Sessions[0]);
        var entries = new List<PersistedSessionIndexEntry>(legacy.Sessions.Count);
        var createdBlobPaths = new List<string>(legacy.Sessions.Count);
        var indexCommitted = false;
        try
        {
            foreach (var session in legacy.Sessions)
            {
                var binding = new PersistedAccountBinding(session.AccountId, session.Endpoints!);
                var revision = Guid.NewGuid();
                var entry = new PersistedSessionIndexEntry(
                    binding.CreateIdentifier(),
                    session.AccountId,
                    session.Email,
                    session.Endpoints!,
                    revision);
                if (!entry.TryNormalize(out entry))
                    throw new InvalidDataException("Legacy session metadata is invalid.");
                var blobPath = GetBlobPath(binding, revision);
                WriteSessionBlob(blobPath, entry, session);
                createdBlobPaths.Add(blobPath);
                entries.Add(entry);
            }

            var index = PersistedSessionIndex.Create(entries);
            WriteIndex(index);
            indexCommitted = true;
            TryCleanupAfterCommit(index);
            return index;
        }
        catch
        {
            if (!indexCommitted)
            {
                foreach (var path in createdBlobPaths)
                    TryDeleteFile(path);
            }
            throw;
        }
    }

    private bool TryReadState(
        out PersistedSessionIndex? index,
        out PersistedSessionCollection? legacy)
    {
        index = null;
        legacy = null;
        byte[] fileBytes;
        try
        {
            fileBytes = ReadBounded(_path, MaximumLegacyFileLength);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        try
        {
            if (HasMagic(fileBytes, IndexMagic))
            {
                if (fileBytes.Length > MaximumIndexFileLength)
                    throw new InvalidDataException("Persisted session index is too large.");
                var entropy = CreateIndexProtectionEntropy();
                byte[]? jsonBytes = null;
                try
                {
                    jsonBytes = UnprotectFramed(
                        fileBytes,
                        IndexMagic,
                        entropy,
                        MaximumIndexPlaintextLength);
                    var loaded = JsonSerializer.Deserialize(
                        jsonBytes,
                        AppJsonContext.Default.PersistedSessionIndex);
                    if (loaded is null || !loaded.TryNormalize(out var normalized))
                        throw new InvalidDataException("Persisted session index is invalid.");
                    index = normalized;
                    return true;
                }
                finally
                {
                    Clear(entropy);
                    Clear(jsonBytes);
                }
            }

            var legacyJson = UnprotectOwned(
                fileBytes,
                entropy: null,
                MaximumLegacyPlaintextLength);
            try
            {
                using var document = JsonDocument.Parse(legacyJson);
                var root = document.RootElement;
                if (root.TryGetProperty("sessions", out _)
                    || root.TryGetProperty("Sessions", out _))
                {
                    var collection = JsonSerializer.Deserialize(
                        legacyJson,
                        AppJsonContext.Default.PersistedSessionCollection);
                    if (collection is null || !collection.TryNormalize(out legacy))
                        throw new InvalidDataException("Legacy persisted session collection is invalid.");
                    return true;
                }

                var session = JsonSerializer.Deserialize(
                    legacyJson,
                    AppJsonContext.Default.PersistedSession);
                if (session is null || !session.TryMigrate(out var normalizedSession))
                    throw new InvalidDataException("Legacy persisted session is invalid.");
                legacy = PersistedSessionCollection.FromMostRecent([normalizedSession]);
                return true;
            }
            finally
            {
                Clear(legacyJson);
            }
        }
        finally
        {
            Clear(fileBytes);
        }
    }

    private bool TryReadSessionBlob(
        PersistedSessionIndexEntry entry,
        out PersistedSession session)
    {
        session = null!;
        var binding = new PersistedAccountBinding(entry.AccountId, entry.Endpoints);
        if (!string.Equals(binding.CreateIdentifier(), entry.BindingId, StringComparison.Ordinal))
            return false;
        var path = GetBlobPath(binding, entry.BlobRevision);
        byte[] fileBytes;
        try
        {
            fileBytes = ReadBounded(path, MaximumBlobFileLength);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        byte[]? entropy = null;
        byte[]? jsonBytes = null;
        try
        {
            if (!HasMagic(fileBytes, BlobMagic))
                return false;
            entropy = binding.CreateBlobProtectionEntropy(entry.BlobRevision);
            jsonBytes = UnprotectFramed(
                fileBytes,
                BlobMagic,
                entropy,
                MaximumBlobPlaintextLength);
            var envelope = JsonSerializer.Deserialize(
                jsonBytes,
                AppJsonContext.Default.PersistedSessionBlobEnvelope);
            return envelope is not null && envelope.TryGetSession(entry, out session);
        }
        finally
        {
            Clear(jsonBytes);
            Clear(entropy);
            Clear(fileBytes);
        }
    }

    private void WriteSessionBlob(
        string path,
        PersistedSessionIndexEntry entry,
        PersistedSession session)
    {
        var binding = new PersistedAccountBinding(entry.AccountId, entry.Endpoints);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            PersistedSessionBlobEnvelope.Create(entry, session),
            AppJsonContext.Default.PersistedSessionBlobEnvelope);
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            EnsureLength(jsonBytes, MaximumBlobPlaintextLength, "Session blob plaintext");
            entropy = binding.CreateBlobProtectionEntropy(entry.BlobRevision);
            protectedBytes = ProtectOwned(jsonBytes, entropy);
            EnsureLength(
                protectedBytes,
                MaximumBlobFileLength - BlobMagic.Length,
                "Protected session blob");
            framedBytes = Frame(BlobMagic, protectedBytes);
            _beforeWrite?.Invoke(TokenStoreWriteKind.SessionBlob, path);
            WriteAtomic(path, framedBytes, overwrite: false);
        }
        finally
        {
            Clear(framedBytes);
            Clear(protectedBytes);
            Clear(entropy);
            Clear(jsonBytes);
        }
    }

    private void WriteIndex(PersistedSessionIndex index)
    {
        if (!index.TryNormalize(out var normalized))
            throw new InvalidDataException("Persisted session index is invalid.");
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            normalized,
            AppJsonContext.Default.PersistedSessionIndex);
        byte[]? entropy = null;
        byte[]? protectedBytes = null;
        byte[]? framedBytes = null;
        try
        {
            EnsureLength(jsonBytes, MaximumIndexPlaintextLength, "Session index plaintext");
            entropy = CreateIndexProtectionEntropy();
            protectedBytes = ProtectOwned(jsonBytes, entropy);
            EnsureLength(
                protectedBytes,
                MaximumIndexFileLength - IndexMagic.Length,
                "Protected session index");
            framedBytes = Frame(IndexMagic, protectedBytes);
            _beforeWrite?.Invoke(TokenStoreWriteKind.SessionIndex, _path);
            WriteAtomic(_path, framedBytes, overwrite: true);
        }
        finally
        {
            Clear(framedBytes);
            Clear(protectedBytes);
            Clear(entropy);
            Clear(jsonBytes);
        }
    }

    private PersistedSessionIndex TryCleanupAfterCommit(PersistedSessionIndex index)
    {
        var latestCommitted = index;
        try
        {
            latestCommitted = CleanupPendingDeletes(index, persistChanges: true);
            CleanupUnreferencedBlobs(latestCommitted);
            TryDeleteTemporaryFiles();
            return latestCommitted;
        }
        catch (Exception exception) when (IsReadableFailure(exception))
        {
            return latestCommitted;
        }
    }

    private PersistedSessionIndex CleanupPendingDeletes(
        PersistedSessionIndex index,
        bool persistChanges)
    {
        var remaining = new List<PersistedSessionPendingDelete>();
        var changed = false;
        foreach (var pending in index.PendingDeletes)
        {
            var remainingRevisions = new List<Guid>();
            foreach (var revision in pending.BlobRevisions)
            {
                var path = GetBlobPath(pending.BindingId, revision);
                _beforeWrite?.Invoke(TokenStoreWriteKind.SessionBlobDelete, path);
                if (TryDeleteFile(path))
                    changed = true;
                else
                    remainingRevisions.Add(revision);
            }
            if (remainingRevisions.Count > 0)
            {
                remaining.Add(new PersistedSessionPendingDelete(
                    pending.BindingId,
                    remainingRevisions));
            }
        }

        if (!changed)
            return index;
        var cleaned = PersistedSessionIndex.Create(index.Accounts, remaining);
        if (persistChanges)
            WriteIndex(cleaned);
        return cleaned;
    }

    private void CleanupUnreferencedBlobs(PersistedSessionIndex index)
    {
        var referenced = index.Accounts
            .Select(account => GetBlobFileName(account.BindingId, account.BlobRevision))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var pending in index.PendingDeletes)
        {
            foreach (var revision in pending.BlobRevisions)
                referenced.Add(GetBlobFileName(pending.BindingId, revision));
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(_blobDirectoryPath, "*.bin"))
            {
                var fileName = Path.GetFileName(path);
                if (IsOwnedBlobFileName(fileName) && !referenced.Contains(fileName))
                    TryDeleteFile(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static IReadOnlyList<PersistedSessionPendingDelete> MergePendingDeletes(
        IEnumerable<PersistedSessionPendingDelete> existing,
        IEnumerable<(string BindingId, Guid Revision)> additions)
    {
        var revisions = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var pending in existing)
            revisions[pending.BindingId] = pending.BlobRevisions.ToHashSet();
        foreach (var (bindingId, revision) in additions)
        {
            if (!PersistedSessionStorageIdentifier.IsBindingId(bindingId)
                || revision == Guid.Empty)
            {
                throw new InvalidDataException("Pending session deletion identifier is invalid.");
            }
            if (!revisions.TryGetValue(bindingId, out var bindingRevisions))
                revisions[bindingId] = bindingRevisions = [];
            bindingRevisions.Add(revision);
        }

        return revisions
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PersistedSessionPendingDelete(
                pair.Key,
                pair.Value.Order().ToArray()))
            .ToArray();
    }

    private IReadOnlyList<Guid> EnumerateBlobRevisions(string bindingId)
    {
        var revisions = new List<Guid>();
        var prefix = bindingId + ".";
        try
        {
            foreach (var path in Directory.EnumerateFiles(_blobDirectoryPath, prefix + "*.bin"))
            {
                var fileName = Path.GetFileName(path);
                if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
                    || !fileName.EndsWith(".bin", StringComparison.Ordinal)
                    || fileName.Length != prefix.Length + 32 + 4)
                {
                    continue;
                }
                var revisionText = fileName.Substring(prefix.Length, 32);
                if (Guid.TryParseExact(revisionText, "N", out var revision)
                    && revision != Guid.Empty)
                {
                    revisions.Add(revision);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        return revisions;
    }

    private IReadOnlyList<string> EnumerateBlobTemporaryPaths(string bindingId)
    {
        var paths = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                _blobDirectoryPath,
                $".{bindingId}.*.tmp"))
            {
                if (TryParseOwnedBlobTemporaryFileName(
                        Path.GetFileName(path),
                        out var candidateBindingId)
                    && string.Equals(candidateBindingId, bindingId, StringComparison.Ordinal))
                {
                    paths.Add(path);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        return paths;
    }

    private bool TryDeleteBlobTemporaryFiles(IEnumerable<string> paths)
    {
        var succeeded = true;
        foreach (var path in paths)
        {
            _beforeWrite?.Invoke(TokenStoreWriteKind.SessionBlobDelete, path);
            succeeded &= TryDeleteFile(path);
        }
        return succeeded;
    }

    private static bool IsOwnedBlobFileName(string fileName)
    {
        if (fileName.Length != 64 + 1 + 32 + 4
            || fileName[64] != '.'
            || !fileName.EndsWith(".bin", StringComparison.Ordinal))
        {
            return false;
        }
        return PersistedSessionStorageIdentifier.IsBindingId(fileName[..64])
            && Guid.TryParseExact(fileName.Substring(65, 32), "N", out var revision)
            && revision != Guid.Empty;
    }

    private static bool IsOwnedBlobTemporaryFileName(string fileName) =>
        TryParseOwnedBlobTemporaryFileName(fileName, out _);

    private static bool TryParseOwnedBlobTemporaryFileName(
        string fileName,
        out string bindingId)
    {
        bindingId = string.Empty;
        if (!fileName.StartsWith(".", StringComparison.Ordinal)
            || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var marker = fileName.IndexOf(".bin.", 1, StringComparison.Ordinal);
        if (marker <= 1)
            return false;

        var blobFileName = fileName.Substring(1, marker + 3);
        if (!IsOwnedBlobFileName(blobFileName))
            return false;

        var suffix = fileName.AsSpan(marker + 5, fileName.Length - marker - 9);
        var separator = suffix.IndexOf('.');
        if (separator <= 0
            || suffix[(separator + 1)..].IndexOf('.') >= 0
            || !IsCanonicalProcessId(suffix[..separator])
            || !Guid.TryParseExact(suffix[(separator + 1)..], "N", out var temporaryId)
            || temporaryId == Guid.Empty)
        {
            return false;
        }

        bindingId = blobFileName[..64];
        return true;
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

    private string GetBlobPath(PersistedAccountBinding binding, Guid revision) =>
        Path.Combine(_blobDirectoryPath, binding.CreateBlobFileName(revision));

    private string GetBlobPath(string bindingId, Guid revision) =>
        Path.Combine(_blobDirectoryPath, GetBlobFileName(bindingId, revision));

    private static string GetBlobFileName(string bindingId, Guid revision)
    {
        if (!PersistedSessionStorageIdentifier.IsBindingId(bindingId)
            || revision == Guid.Empty)
        {
            throw new InvalidDataException("Session blob identifier is invalid.");
        }
        return $"{bindingId}.{revision:N}.bin";
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
                throw new IOException("Timed out acquiring the persisted-session store lock.");
            }
        }
    }

    private byte[] ReadBounded(string path, int maximumLength)
    {
        if (_reader is not null)
        {
            var injected = _reader(path);
            try
            {
                EnsureLength(injected, maximumLength, "Persisted session file");
                return injected;
            }
            catch
            {
                Clear(injected);
                throw;
            }
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumLength)
            throw new InvalidDataException("Persisted session file length is invalid.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Persisted session file changed while being read.");
            return bytes;
        }
        catch
        {
            Clear(bytes);
            throw;
        }
    }

    private byte[] ProtectOwned(byte[] plaintext, byte[]? entropy)
    {
        var protectedBytes = _protection.Protect(plaintext, entropy);
        if (protectedBytes is null)
            throw new CryptographicException("The data protector returned no protected payload.");
        if (ReferenceEquals(protectedBytes, plaintext)
            || ReferenceEquals(protectedBytes, entropy))
        {
            throw new CryptographicException(
                "The data protector returned an aliased protected payload.");
        }
        return protectedBytes;
    }

    private byte[] UnprotectOwned(
        byte[] protectedBytes,
        byte[]? entropy,
        int maximumLength)
    {
        var plaintext = _protection.Unprotect(protectedBytes, entropy);
        if (plaintext is null)
            throw new CryptographicException("The data protector returned no plaintext.");
        var owned = ReferenceEquals(plaintext, protectedBytes)
            || ReferenceEquals(plaintext, entropy)
                ? plaintext.ToArray()
                : plaintext;
        try
        {
            EnsureLength(owned, maximumLength, "Persisted session plaintext");
            return owned;
        }
        catch
        {
            Clear(owned);
            throw;
        }
    }

    private byte[] UnprotectFramed(
        byte[] framedBytes,
        ReadOnlySpan<byte> magic,
        byte[] entropy,
        int maximumLength)
    {
        if (!HasMagic(framedBytes, magic) || framedBytes.Length == magic.Length)
            throw new InvalidDataException("Persisted session file header is invalid.");
        var protectedBytes = framedBytes.AsSpan(magic.Length).ToArray();
        try
        {
            return UnprotectOwned(protectedBytes, entropy, maximumLength);
        }
        finally
        {
            Clear(protectedBytes);
        }
    }

    private static byte[] CreateIndexProtectionEntropy()
    {
        var domain = Encoding.UTF8.GetBytes(IndexEntropyDomain);
        try
        {
            return SHA256.HashData(domain);
        }
        finally
        {
            Clear(domain);
        }
    }

    private static byte[] Frame(ReadOnlySpan<byte> magic, byte[] protectedBytes)
    {
        var framed = GC.AllocateUninitializedArray<byte>(magic.Length + protectedBytes.Length);
        magic.CopyTo(framed);
        protectedBytes.CopyTo(framed, magic.Length);
        return framed;
    }

    private static bool HasMagic(byte[] bytes, ReadOnlySpan<byte> magic) =>
        bytes.Length >= magic.Length
        && bytes.AsSpan(0, magic.Length).SequenceEqual(magic);

    private static void EnsureLength(byte[] bytes, int maximumLength, string description)
    {
        if (bytes.Length is <= 0 || bytes.Length > maximumLength)
            throw new InvalidDataException($"{description} length is invalid.");
    }

    private void WriteAtomic(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("Persisted session path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
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
            File.Move(temporaryPath, path, overwrite);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
                TryDeleteFile(temporaryPath);
        }
    }

    private bool TryDeleteFile(string path)
    {
        try
        {
            DeleteFile(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void DeleteFileIfPresent(string path)
    {
        try
        {
            DeleteFile(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void DeleteFile(string path)
    {
        if (_deleteFile is null)
            File.Delete(path);
        else
            _deleteFile(path);
    }

    private bool TryDeleteIndexTemporaryFiles()
    {
        var succeeded = TryDeleteFile(_path + ".tmp");
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                _directoryPath,
                $".{Path.GetFileName(_path)}.*.tmp"))
            {
                succeeded &= TryDeleteFile(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return succeeded;
    }

    private void TryDeleteTemporaryFiles()
    {
        TryDeleteIndexTemporaryFiles();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_blobDirectoryPath, ".*.tmp"))
            {
                if (IsOwnedBlobTemporaryFileName(Path.GetFileName(path)))
                    TryDeleteFile(path);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void TryDeleteBlobDirectory()
    {
        try
        {
            Directory.Delete(_blobDirectoryPath, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }

    private static bool IsReadableFailure(Exception exception) => exception is
        PlatformNotSupportedException
        or CryptographicException
        or JsonException
        or IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or ArgumentException;
}

internal enum TokenStoreWriteKind
{
    SessionBlob,
    SessionIndex,
    SessionBlobDelete,
}

internal interface ITokenStoreProtectionPolicy
{
    bool IsSupported { get; }

    byte[] Protect(byte[] plaintext, byte[]? entropy);

    byte[] Unprotect(byte[] protectedData, byte[]? entropy);
}

internal sealed class CurrentUserDpapiTokenStoreProtection : ITokenStoreProtectionPolicy
{
    public static CurrentUserDpapiTokenStoreProtection Instance { get; } = new();

    private CurrentUserDpapiTokenStoreProtection()
    {
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plaintext, byte[]? entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only supported on Windows.");
        return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData, byte[]? entropy)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only supported on Windows.");
        return ProtectedData.Unprotect(protectedData, entropy, DataProtectionScope.CurrentUser);
    }
}
