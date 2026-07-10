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

    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        return SyncCoreAsync(lease.Origin.Value, ct);
    }

    // 拉取 + 解密 + 存快照的核心逻辑,按 serverUrl 参数化(唯一实现处)。
    // serverUrl 显式传入:首次 bootstrap 时 _session.Account 尚未就绪(ServerUrl 为空),
    // 必须由调用方(VaultBootstrapper)提供登录/解锁时已知的 serverUrl,以正确构建 Account.ServerUrl。
    public async Task<IReadOnlyList<Cipher>> SyncCoreAsync(string serverUrl, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        var api = _clients.GetAuthenticated<IReadonlyApiClient>(lease);
        var sync = await api.GetSyncAsync(linked.Token);
        _session.ThrowIfExpired(lease);
        var vault = _decryptor.Decrypt(sync, userKey, serverUrl);
        // 若在途同步期间被并发 Lock/Clear(如登出),SetSnapshot 返回 false;此时**不得**再落盘缓存,
        // 否则会把登出刚 Clear 掉的 vault-cache.bin 复写回来(I1)。仅在快照实际应用时刷新私钥+缓存。
        if (_session.SetSnapshot(vault, dataStale: false))
        {
            _session.SetEncryptedPrivateKey(sync.Profile?.PrivateKey);
            // 成功同步后刷新离线缓存(P1-1)。email 取自 profile,与解锁时的 persisted.Email 一致。
            _cache.Save(sync, sync.Profile?.Email ?? string.Empty, serverUrl, DateTimeOffset.UtcNow);
        }
        return vault.Ciphers;
    }
}
