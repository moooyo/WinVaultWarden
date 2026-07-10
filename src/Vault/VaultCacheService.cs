using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Session;
using Crypto;

namespace Vault;

/// <summary>
/// 密码库缓存编排:加密态 SyncResponse ↔ JSON(ApiJsonContext) + 头部校验 + 解密进快照。
/// 使 Core/ICacheStore 不感知 Api 类型;解锁离线路径与同步写缓存都经此。
/// 所有读写均绑定当前会话租约(account/origin/generation),校验通过后经 TryCommit 原子提交。
/// </summary>
public sealed class VaultCacheService
{
    public const int SchemaVersion = 2;

    private readonly ICacheStore _store;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;

    public VaultCacheService(ICacheStore store, VaultDecryptor decryptor, VaultSession session)
    {
        _store = store;
        _decryptor = decryptor;
        _session = session;
    }

    /// <summary>在租约仍当前时把加密态 SyncResponse 写入缓存(经 TryCommit 原子门禁)。</summary>
    public bool Save(
        VaultSessionLease lease,
        SyncResponse sync,
        DateTimeOffset cachedAt) =>
        _session.TryCommit(lease, () => SaveCurrent(lease, sync, cachedAt));

    /// <summary>
    /// 直接写缓存(不做租约门禁),供 <see cref="SyncService"/> 在其自身 TryCommit 内联调用,
    /// 使缓存与快照/私钥落在同一原子门禁中。可能触发持久化 IO,失败则整体门禁抛出。
    /// </summary>
    internal void SaveCurrent(
        VaultSessionLease lease,
        SyncResponse sync,
        DateTimeOffset cachedAt)
    {
        var json = JsonSerializer.Serialize(sync, ApiJsonContext.Default.SyncResponse);
        _store.SaveVaultCache(new VaultCacheEntry(
            SchemaVersion,
            lease.AccountId,
            lease.Origin,
            lease.Generation,
            sync.Profile?.Email ?? string.Empty,
            cachedAt,
            json));
    }

    /// <summary>
    /// 载入缓存并解密进快照(DataStale=true)。校验 schema/account/origin/generation 头部与
    /// profile.Id 是否等于租约账户;任何不符/损坏返回 false。最终写入经 TryCommit 门禁,
    /// 若在解密期间被并发 Lock/Logout/Switch,快照与私钥都不落地。
    /// </summary>
    public bool TryLoadInto(
        VaultSessionLease lease,
        SymmetricCryptoKey userKey)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (!_store.TryLoadVaultCache(out var entry)
            || entry.SchemaVersion != SchemaVersion
            || entry.AccountId != lease.AccountId
            || entry.Origin != lease.Origin
            || entry.Generation != lease.Generation)
            return false;

        try
        {
            var sync = JsonSerializer.Deserialize(
                entry.SyncResponseJson,
                ApiJsonContext.Default.SyncResponse);
            if (sync?.Profile?.Id != lease.AccountId)
                return false;
            var vault = _decryptor.Decrypt(sync, userKey, lease.Origin.Value);
            return _session.TryCommit(lease, () =>
            {
                _session.SetEncryptedPrivateKey(sync.Profile.PrivateKey);
                _session.SetSnapshot(vault, dataStale: true);
            });
        }
        catch (Exception ex) when (
            ex is JsonException
                or CryptographicException
                or FormatException
                or ArgumentException
                or InvalidOperationException)
        {
            // 缓存加载/解密的任何失败(损坏/格式不符/意外异常)→ 视为无缓存,回退联网。
            // 在解锁(auth)路径上宁可回退也不外抛:此时会话已 SetUnlockedKey,外抛会留悬挂态。
            return false;
        }
    }

    /// <summary>
    /// 把既有缓存条目的 generation 重绑到当前租约(仅校验 schema/account/origin)。
    /// 用于会话 generation 轮换后(如同会话内 Lock/Unlock)让旧缓存重新对齐当前租约,
    /// 供随后的 <see cref="TryLoadInto"/> 命中。经 TryCommit 门禁原子写回。
    /// </summary>
    public bool RebindGeneration(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (!_store.TryLoadVaultCache(out var entry)
            || entry.SchemaVersion != SchemaVersion
            || entry.AccountId != lease.AccountId
            || entry.Origin != lease.Origin)
            return false;
        return _session.TryCommit(lease, () =>
            _store.SaveVaultCache(entry with { Generation = lease.Generation }));
    }

    public void Clear() => _store.ClearVaultCache();
}
