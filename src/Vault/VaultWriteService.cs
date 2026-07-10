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
    private readonly IVaultApiClientProvider _clients;
    private readonly CipherEncryptor _encryptor;
    private readonly ISyncService _sync;
    private readonly VaultSession _session;

    public VaultWriteService(IVaultApiClientProvider clients, CipherEncryptor encryptor, ISyncService sync, VaultSession session)
    {
        _clients = clients;
        _encryptor = encryptor;
        _sync = sync;
        _session = session;
    }

    public async Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        var request = _encryptor.Encrypt(cipher, userKey);
        if (string.IsNullOrEmpty(cipher.Id))
            await api.CreateCipherAsync(request, linked.Token);
        else
            await api.UpdateCipherAsync(cipher.Id, request, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    public async Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default)
    {
        RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        if (permanent)
            await api.HardDeleteCipherAsync(cipherId, linked.Token);
        else
            await api.SoftDeleteCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    public async Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
    {
        RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        await api.RestoreCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    public async Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        var userKey = RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        var request = new FolderRequest { Name = _encryptor.EncryptFolderName(name, userKey) };
        if (string.IsNullOrEmpty(folderId))
            await api.CreateFolderAsync(request, linked.Token);
        else
            await api.UpdateFolderAsync(folderId, request, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    public async Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        RequireUserKey();
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        await api.DeleteFolderAsync(folderId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    public async Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
    {
        RequireUserKey();
        if (ids.Count == 0) return;
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        await api.BulkMoveCiphersAsync(ids, string.IsNullOrWhiteSpace(folderId) ? null : folderId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    public async Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
    {
        RequireUserKey();
        if (ids.Count == 0) return;
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        if (permanent)
            await api.BulkHardDeleteCiphersAsync(ids, linked.Token);
        else
            await api.BulkSoftDeleteCiphersAsync(ids, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    public async Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        RequireUserKey();
        if (ids.Count == 0) return;
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        await api.BulkRestoreCiphersAsync(ids, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(ct);
    }

    private SymmetricCryptoKey RequireUserKey() =>
        _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");

    // 写后整库重同步:写已在服务端成功,若此处刷新失败,变更仍已持久化,本地快照可能过期。
    // 包装为 VaultRefreshException 让上层区分"写失败"(重试)与"写成功但刷新失败"(手动刷新)。
    private async Task ResyncAsync(CancellationToken ct)
    {
        try
        {
            await _sync.SyncAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VaultRefreshException("更改已保存,但刷新本地数据失败,请手动刷新。", ex);
        }
    }
}
