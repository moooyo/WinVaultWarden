using Api;
using Api.Dtos;
using Core.Services;
using Core.Session;
using Crypto;
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
    {
        try
        {
            var data = ParseContent(format, content);
            return new ImportPreview(data.Ciphers.Count, data.Folders.Count, null);
        }
        catch (Exception ex)
        {
            return new ImportPreview(0, 0, $"无法解析文件: {ex.Message}");
        }
    }

    public async Task<int> ImportAsync(ImportFormat format, string content, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var capabilities = _session.GetCapabilities(lease);
        VaultPolicyEvaluator.ThrowIfEnabled(
            _session,
            Core.Enums.OrganizationPolicyType.PersonalOwnership,
            "An organization policy prevents importing items into the personal vault.");
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
            ciphers = copiedKey.Legacy is { } legacy
                ? data.Ciphers
                    .Select(cipher => _encryptor.Encrypt(
                        cipher.WithFolderId(null),
                        legacy,
                        capabilities).WithEncryptionContext(lease.AccountId))
                    .ToArray()
                : data.Ciphers
                    .Select(cipher => _encryptor.Encrypt(
                        cipher.WithFolderId(null),
                        copiedKey.V2!,
                        capabilities).WithEncryptionContext(lease.AccountId))
                    .ToArray();
            folders = copiedKey.Legacy is { } folderLegacy
                ? data.Folders.Select(folder => new FolderRequest
                {
                    Name = _encryptor.EncryptFolderName(folder.Name, folderLegacy),
                }).ToArray()
                : data.Folders.Select(folder => new FolderRequest
                {
                    Name = _encryptor.EncryptFolderName(folder.Name, copiedKey.V2!),
                }).ToArray();
        }
        var rels = data.Relations.Select(r => new ImportRelationship { Key = r.CipherIndex, Value = r.FolderIndex }).ToArray();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.ImportCiphersAsync(new ImportRequest { Ciphers = ciphers, Folders = folders, FolderRelationships = rels }, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);

        return data.Ciphers.Count;
    }

    private static PortingData ParseContent(ImportFormat format, string content) => format switch
    {
        ImportFormat.Json => BitwardenJsonCodec.Parse(content),
        ImportFormat.Csv => BitwardenCsvCodec.Parse(content),
        _ => throw new NotSupportedException($"Unsupported import format: {format}"),
    };

    // 写后整库重同步:导入已在服务端成功,若刷新失败则本地快照可能过期。
    // 包装为 VaultRefreshException 让上层区分"导入失败"与"导入成功但刷新失败"。
    private async Task ResyncAsync(VaultSessionLease lease, CancellationToken ct)
    {
        try
        {
            await _sync.SyncAsync(lease, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new VaultRefreshException("导入已完成,但刷新本地数据失败,请手动刷新。", ex);
        }
    }
}
