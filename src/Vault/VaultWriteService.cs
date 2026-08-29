using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Core.Session;
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

    public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) =>
        SaveCipherAsync(cipher, _session.CaptureLease(), ct);

    // lease-bound:以调用方传入的原始 lease 门禁。跨加密/写 API/同步的每个 await 边界前后都校验
    // 该 lease 仍对应当前会话——一旦 Lock/切换账户使其失效,立即抛 OperationCanceledException,
    // 绝不把凭据写入或同步到别的账户(A 不能写 B)。
    public async Task SaveCipherAsync(Cipher cipher, VaultSessionLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        _session.ThrowIfExpired(lease);
        if (!_session.TryCopyUserKey(lease, out var userKey) || userKey is null)
            throw new OperationCanceledException(lease.CancellationToken);

        CipherRequest request;
        using (userKey)
            request = _encryptor.Encrypt(cipher, userKey);

        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (string.IsNullOrEmpty(cipher.Id))
            await api.CreateCipherAsync(request, linked.Token);
        else
            await api.UpdateCipherAsync(cipher.Id, request, linked.Token);

        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (permanent)
            await api.HardDeleteCipherAsync(cipherId, linked.Token);
        else
            await api.SoftDeleteCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.RestoreCipherAsync(cipherId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey) || copiedKey is null)
            throw new InvalidOperationException("Vault is locked.");
        FolderRequest request;
        using (copiedKey)
            request = new FolderRequest { Name = _encryptor.EncryptFolderName(name, copiedKey) };

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        _session.ThrowIfExpired(lease);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (string.IsNullOrEmpty(folderId))
            await api.CreateFolderAsync(request, linked.Token);
        else
            await api.UpdateFolderAsync(folderId, request, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.DeleteFolderAsync(folderId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        if (ids.Count == 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.BulkMoveCiphersAsync(ids, string.IsNullOrWhiteSpace(folderId) ? null : folderId, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        if (ids.Count == 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        if (permanent)
            await api.BulkHardDeleteCiphersAsync(ids, linked.Token);
        else
            await api.BulkSoftDeleteCiphersAsync(ids, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    public async Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        RequireUnlocked(lease);
        if (ids.Count == 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IVaultWriteApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.BulkRestoreCiphersAsync(ids, linked.Token);
        _session.ThrowIfExpired(lease);
        await ResyncAsync(lease, ct);
    }

    private void RequireUnlocked(VaultSessionLease lease)
    {
        if (!_session.IsUnlocked(lease))
            throw new InvalidOperationException("Vault is locked.");
    }

    // 写后整库重同步:写已在服务端成功,若此处刷新失败,变更仍已持久化,本地快照可能过期。
    // 包装为 VaultRefreshException 让上层区分"写失败"(重试)与"写成功但刷新失败"(手动刷新)。
    // 以请求原始 lease 同步,确保不会把结果落到已切换的账户上。
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
            throw new VaultRefreshException("更改已保存,但刷新本地数据失败,请手动刷新。", ex);
        }
    }
}
