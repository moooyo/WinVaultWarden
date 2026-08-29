using Api;
using Core.Models;
using Core.Services;
using Core.Session;

namespace Vault;

public sealed class SyncService : ISyncService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;
    private readonly VaultCacheService _cache;

    public SyncService(IVaultApiClientProvider clients, VaultDecryptor decryptor, VaultSession session, VaultCacheService cache)
    {
        _clients = clients;
        _decryptor = decryptor;
        _session = session;
        _cache = cache;
    }

    // 兼容入口:捕获一次当前 lease 后委托给 lease 重载。
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
        SyncAsync(_session.CaptureLease(), ct);

    public async Task<IReadOnlyList<Cipher>> SyncAsync(
        VaultSessionLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // 复制一份 UserKey 交由本次操作独占,租约失效则直接拒绝(会话已 Lock/Logout/Switch)。
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey;

        // linked token 只做快速取消(合作式);最终门禁不依赖它,由 TryCommit 原子校验租约。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IReadonlyApiClient>(lease);
        var sync = await api.GetSyncAsync(linked.Token).ConfigureAwait(false);

        // 账户错配:下发的 profile 必须属于当前租约账户,否则整体拒绝(不写缓存/快照/私钥)。
        if (!string.Equals(sync.Profile?.Id, lease.AccountId, StringComparison.Ordinal))
            throw new InvalidDataException("Sync profile does not match the active account.");

        var vault = _decryptor.Decrypt(sync, userKey!, lease.Endpoints);

        // 缓存、快照、私钥在同一 TryCommit 原子门禁内提交:要么全落地、要么全不落地。
        // 写缓存(可能触发 DPAPI/磁盘 IO)排在不会抛出的内存赋值之前——若持久化失败,
        // 快照与私钥也不更新,避免半提交;若租约已失效则整体丢弃(抛取消)。
        if (!_session.TryCommit(lease, () =>
        {
            _cache.SaveCurrent(lease, sync, DateTimeOffset.UtcNow);
            _session.SetEncryptedPrivateKey(sync.Profile?.PrivateKey);
            _session.SetSnapshot(vault, dataStale: false);
        }))
            throw new OperationCanceledException(
                "Sync result belongs to an expired session.",
                lease.CancellationToken);

        return vault.Ciphers;
    }
}
