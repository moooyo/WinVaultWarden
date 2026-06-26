using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// 写操作编排器:加密(如需)→ 调用写 API → 整库重新同步。
// 写后整库重拉,保证本地快照与服务端一致(设计决策,见 spec)。
public sealed class VaultWriteService : IVaultWriteService
{
    private readonly IVaultWriteApiClient _api;
    private readonly CipherEncryptor _encryptor;
    private readonly ISyncService _sync;
    private readonly VaultSession _session;

    public VaultWriteService(IVaultWriteApiClient api, CipherEncryptor encryptor, ISyncService sync, VaultSession session)
    {
        _api = api;
        _encryptor = encryptor;
        _sync = sync;
        _session = session;
    }

    public async Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var request = _encryptor.Encrypt(cipher, userKey);
        if (string.IsNullOrEmpty(cipher.Id))
            await _api.CreateCipherAsync(request, ct);
        else
            await _api.UpdateCipherAsync(cipher.Id, request, ct);
        await _sync.SyncAsync(ct);
    }

    public async Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default)
    {
        RequireUserKey();
        if (permanent)
            await _api.HardDeleteCipherAsync(cipherId, ct);
        else
            await _api.SoftDeleteCipherAsync(cipherId, ct);
        await _sync.SyncAsync(ct);
    }

    public async Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
    {
        RequireUserKey();
        await _api.RestoreCipherAsync(cipherId, ct);
        await _sync.SyncAsync(ct);
    }

    public async Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var request = new FolderRequest { Name = _encryptor.EncryptFolderName(name, userKey) };
        if (string.IsNullOrEmpty(folderId))
            await _api.CreateFolderAsync(request, ct);
        else
            await _api.UpdateFolderAsync(folderId, request, ct);
        await _sync.SyncAsync(ct);
    }

    public async Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        RequireUserKey();
        await _api.DeleteFolderAsync(folderId, ct);
        await _sync.SyncAsync(ct);
    }

    private SymmetricCryptoKey RequireUserKey() =>
        _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
}
