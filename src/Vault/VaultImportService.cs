using Api;
using Api.Dtos;
using Core.Services;
using Core.Session;
using Crypto;
using Core.Models;
using System.Text;
using Vault.Porting;

namespace Vault;

// 导入编排器:解析导出文件(JSON/CSV)→ 加密 → 批量调用 /ciphers/import → 整库重新同步。
// 写后整库重拉,保证本地快照与服务端一致(与 VaultWriteService 一致的设计决策)。
public sealed class VaultImportService : IVaultImportService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly CipherEncryptor _encryptor;
    private readonly ISyncService _sync;
    private readonly VaultSession _session;

    public VaultImportService(IVaultApiClientProvider clients, CipherEncryptor encryptor, ISyncService sync, VaultSession session)
    {
        _clients = clients;
        _encryptor = encryptor;
        _sync = sync;
        _session = session;
    }

    public ImportPreview Parse(ImportFormat format, string content)
        => Parse(format, content, new VaultImportOptions());

    public ImportPreview Parse(ImportFormat format, string content, VaultImportOptions options)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            var data = ParseContent(format, content);
            if (options.DestinationFolderId is not null)
            {
                ValidateDestination(_session.CaptureLease(), options.DestinationFolderId);
                return new ImportPreview(data.Ciphers.Count, 0, null);
            }
            return new ImportPreview(data.Ciphers.Count, data.Folders.Count, null);
        }
        catch (Exception ex)
        {
            return new ImportPreview(0, 0, $"无法解析文件: {ex.Message}");
        }
    }

    public IReadOnlyList<Folder> GetDestinationFolders()
    {
        try
        {
            var lease = _session.CaptureLease();
            Folder[] folders = [];
            return _session.TryRunIfUnlocked(lease, () => folders = _session.Folders.ToArray())
                ? folders
                : [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    public async Task<string> PrepareFileAsync(
        ImportFormat format,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default)
    {
        if ((ulong)bytes.Length > VaultPortingLimits.MaximumFileSizeBytes)
            throw new InvalidDataException("The selected source file exceeds the import size limit.");
        var lease = _session.CaptureLease();
        if (!_session.IsUnlocked(lease))
            throw new InvalidOperationException("The vault must be unlocked before preparing an import.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var json = await Task.Run(() =>
        {
            linked.Token.ThrowIfCancellationRequested();
            var data = format == ImportFormat.OnePasswordArchive
                ? OnePasswordArchiveImportCodec.Parse(bytes, linked.Token)
                : ParseContent(format, DecodeText(bytes.Span));
            linked.Token.ThrowIfCancellationRequested();
            var normalizedFolders = data.Folders.Select((folder, index) => new Folder
            {
                Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = folder.Name,
                RevisionDate = folder.RevisionDate,
            }).ToArray();
            var relations = data.Relations.ToDictionary(pair => pair.CipherIndex, pair => pair.FolderIndex);
            var normalizedCiphers = data.Ciphers.Select((cipher, index) => cipher.WithFolderId(
                relations.TryGetValue(index, out var folderIndex)
                    && folderIndex >= 0 && folderIndex < normalizedFolders.Length
                        ? normalizedFolders[folderIndex].Id
                        : null)).ToArray();
            var canonical = BitwardenJsonCodec.Serialize(normalizedCiphers, normalizedFolders);
            VaultPortingLimits.ValidateContent(canonical);
            return canonical;
        }, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        _session.ThrowIfExpired(lease);
        return json;
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        string text;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            text = new UnicodeEncoding(false, true, true).GetString(bytes[2..]);
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            text = new UnicodeEncoding(true, true, true).GetString(bytes[2..]);
        else
        {
            if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                bytes = bytes[3..];
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        VaultPortingLimits.ValidateContent(text);
        return text;
    }

    public ImportProtection InspectJsonProtection(string content)
    {
        VaultPortingLimits.ValidateContent(content);
        return EncryptedBitwardenJsonCodec.ReadProtection(content);
    }

    public async Task<string> UnlockJsonAsync(
        string content,
        string? password,
        CancellationToken ct = default)
    {
        var protection = InspectJsonProtection(content);
        ct.ThrowIfCancellationRequested();
        if (protection == ImportProtection.None)
            return content;

        var lease = _session.CaptureLease();
        if (!_session.IsUnlocked(lease))
            throw new InvalidOperationException("The vault must be unlocked before decrypting a backup.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        string plaintext;
        if (protection == ImportProtection.Password)
        {
            if (string.IsNullOrEmpty(password) || password.Length > 1024)
                throw new ArgumentException("A backup password of at most 1024 characters is required.", nameof(password));
            plaintext = await Task.Run(
                () => EncryptedBitwardenJsonCodec.DecryptWithPassword(content, password),
                linked.Token).ConfigureAwait(false);
        }
        else
        {
            if (!_session.TryCopyUserKeyMaterial(lease, out var key) || key is null)
                throw new InvalidOperationException("The current account key is unavailable.");
            using (key)
            {
                plaintext = await Task.Run(
                    () => EncryptedBitwardenJsonCodec.DecryptAccountRestricted(content, key),
                    linked.Token).ConfigureAwait(false);
            }
        }

        linked.Token.ThrowIfCancellationRequested();
        _session.ThrowIfExpired(lease);
        VaultPortingLimits.ValidateContent(plaintext);
        _ = BitwardenJsonCodec.Parse(plaintext);
        return plaintext;
    }

    public Task<int> ImportAsync(ImportFormat format, string content, CancellationToken ct = default) =>
        ImportAsync(format, content, new VaultImportOptions(), ct);

    public async Task<int> ImportAsync(
        ImportFormat format,
        string content,
        VaultImportOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var destination = options.DestinationFolderId is null
            ? null
            : ValidateDestination(lease, options.DestinationFolderId);
        var capabilities = _session.GetCapabilities(lease);
        VaultPolicyEvaluator.ThrowIfEnabled(
            _session,
            Core.Enums.OrganizationPolicyType.PersonalOwnership,
            "An organization policy prevents importing items into the personal vault.");
        var prepared = await Task.Run(() =>
        {
            linked.Token.ThrowIfCancellationRequested();
            var data = ParseContent(format, content);
            foreach (var cipher in data.Ciphers)
                CipherWritePolicy.EnsureCanWrite(cipher, capabilities);
            if (data.Ciphers.Any(cipher => VaultPolicyEvaluator.IsCipherRestricted(_session, cipher)))
                throw new OrganizationPolicyException("An organization policy restricts one or more imported item types.");
            if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey) || copiedKey is null)
                throw new InvalidOperationException("Vault is locked.");

            CipherRequest[] ciphers;
            FolderRequest[] folders;
            using (copiedKey)
            {
                ciphers = data.Ciphers.Select(cipher =>
                {
                    linked.Token.ThrowIfCancellationRequested();
                    var normalized = ImportCipherNormalizer.AsNewPersonalCipher(cipher);
                    return (copiedKey.Legacy is { } legacy
                        ? _encryptor.Encrypt(normalized, legacy, capabilities)
                        : _encryptor.Encrypt(normalized, copiedKey.V2!, capabilities))
                        .WithEncryptionContext(lease.AccountId);
                }).ToArray();
                folders = copiedKey.Legacy is { } folderLegacy
                    ? data.Folders.Select(folder => new FolderRequest
                    {
                        Name = _encryptor.EncryptFolderName(folder.Name, folderLegacy),
                    }).ToArray()
                    : data.Folders.Select(folder => new FolderRequest
                    {
                        Name = _encryptor.EncryptFolderName(folder.Name, copiedKey.V2!),
                    }).ToArray();
                if (destination is not null)
                {
                    folders = [new FolderRequest
                    {
                        Id = destination.Id,
                        Name = copiedKey.Legacy is { } destinationKey
                            ? _encryptor.EncryptFolderName(destination.Name, destinationKey)
                            : _encryptor.EncryptFolderName(destination.Name, copiedKey.V2!),
                    }];
                }
            }
            var rels = destination is null
                ? data.Relations.Select(r => new ImportRelationship { Key = r.CipherIndex, Value = r.FolderIndex }).ToArray()
                : data.Ciphers.Select((_, index) => new ImportRelationship { Key = index, Value = 0 }).ToArray();
            return (Request: new ImportRequest { Ciphers = ciphers, Folders = folders, FolderRelationships = rels }, Count: data.Ciphers.Count);
        }, linked.Token).ConfigureAwait(false);
        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (destination is not null)
            ValidateDestination(lease, destination.Id);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            await api.ImportCiphersAsync(prepared.Request, linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException or TimeoutException or OperationCanceledException)
        {
            // Vaultwarden creates folders and then writes ciphers sequentially. Even an
            // HTTP error can follow a partial write; a lost response cannot be treated as
            // proof that nothing changed. Pre-dispatch gates stay outside this catch.
            throw new VaultImportOutcomeUncertainException(prepared.Count, exception);
        }
        await ResyncAsync(lease, ct);

        return prepared.Count;
    }

    private static PortingData ParseContent(ImportFormat format, string content)
    {
        VaultPortingLimits.ValidateContent(content);
        return format switch
        {
            ImportFormat.Json => BitwardenJsonCodec.Parse(content),
            ImportFormat.Csv => BitwardenCsvCodec.Parse(content),
            ImportFormat.KeePassXml => KeePassXmlImportCodec.Parse(content),
            ImportFormat.ChromeCsv => BrowserCsvImportCodec.Parse(BrowserCsvSource.Chrome, content),
            ImportFormat.EdgeCsv => BrowserCsvImportCodec.Parse(BrowserCsvSource.Edge, content),
            ImportFormat.FirefoxCsv => BrowserCsvImportCodec.Parse(BrowserCsvSource.Firefox, content),
            ImportFormat.LastPassCsv => BrowserCsvImportCodec.Parse(BrowserCsvSource.LastPass, content),
            ImportFormat.OnePasswordArchive => throw new InvalidDataException("A 1Password archive must be read as a binary source file before preview."),
            _ => throw new NotSupportedException($"Unsupported import format: {format}"),
        };
    }

    private Folder ValidateDestination(VaultSessionLease lease, string folderId)
    {
        if (string.IsNullOrWhiteSpace(folderId) || folderId.Length > 256)
            throw new InvalidDataException("The destination folder identifier is invalid.");
        Folder? destination = null;
        if (!_session.TryRunIfUnlocked(lease, () =>
            destination = _session.Folders.SingleOrDefault(folder => string.Equals(folder.Id, folderId, StringComparison.Ordinal))))
            throw new OperationCanceledException(lease.CancellationToken);
        return destination ?? throw new InvalidDataException("The destination is not a folder in the currently unlocked personal vault.");
    }

    // 写后整库重同步:导入已在服务端成功,若刷新失败则本地快照可能过期。
    // 包装为 VaultRefreshException 让上层区分"导入失败"与"导入成功但刷新失败"。
    private async Task ResyncAsync(VaultSessionLease lease, CancellationToken ct)
    {
        try
        {
            _session.ThrowIfExpired(lease);
            await _sync.SyncAsync(lease, ct);
            _session.ThrowIfExpired(lease);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The import endpoint has already acknowledged the complete write. Cancellation,
            // timeout, lock and account switching cannot turn that fact into a retryable failure.
            throw new VaultRefreshException("The import completed, but local refresh did not complete.", ex);
        }
    }
}
