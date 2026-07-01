using Api;
using Api.Dtos;
using Core.Services;
using Crypto;
using Vault.Porting;

namespace Vault;

// 导入编排器:解析导出文件(JSON/CSV)→ 加密 → 批量调用 /ciphers/import → 整库重新同步。
// 写后整库重拉,保证本地快照与服务端一致(与 VaultWriteService 一致的设计决策)。
public sealed class VaultImportService : IVaultImportService
{
    private readonly IVaultWriteApiClient _api;
    private readonly CipherEncryptor _encryptor;
    private readonly ISyncService _sync;
    private readonly VaultSession _session;

    public VaultImportService(IVaultWriteApiClient api, CipherEncryptor encryptor, ISyncService sync, VaultSession session)
    {
        _api = api;
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
        var userKey = RequireUserKey();
        var data = ParseContent(format, content);

        var ciphers = data.Ciphers.Select(c => _encryptor.Encrypt(c, userKey)).ToArray();
        var folders = data.Folders.Select(f => new FolderRequest { Name = _encryptor.EncryptFolderName(f.Name, userKey) }).ToArray();
        var rels = data.Relations.Select(r => new ImportRelationship { Key = r.CipherIndex, Value = r.FolderIndex }).ToArray();

        await _api.ImportCiphersAsync(new ImportRequest { Ciphers = ciphers, Folders = folders, FolderRelationships = rels }, ct);
        await _sync.SyncAsync(ct);

        return data.Ciphers.Count;
    }

    private static PortingData ParseContent(ImportFormat format, string content) => format switch
    {
        ImportFormat.Json => BitwardenJsonCodec.Parse(content),
        ImportFormat.Csv => BitwardenCsvCodec.Parse(content),
        _ => throw new NotSupportedException($"Unsupported import format: {format}"),
    };

    private SymmetricCryptoKey RequireUserKey() =>
        _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
}
