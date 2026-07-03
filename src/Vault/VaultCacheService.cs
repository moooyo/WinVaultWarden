using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Crypto;

namespace Vault;

/// <summary>
/// 密码库缓存编排:加密态 SyncResponse ↔ JSON(ApiJsonContext) + 头部校验 + 解密进快照。
/// 使 Core/ICacheStore 不感知 Api 类型;解锁离线路径与同步写缓存都经此。
/// </summary>
public sealed class VaultCacheService
{
    public const int SchemaVersion = 1;

    private readonly ICacheStore _store;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;

    public VaultCacheService(ICacheStore store, VaultDecryptor decryptor, VaultSession session)
    {
        _store = store;
        _decryptor = decryptor;
        _session = session;
    }

    public void Save(SyncResponse sync, string email, string serverUrl, DateTimeOffset cachedAt)
    {
        var json = JsonSerializer.Serialize(sync, ApiJsonContext.Default.SyncResponse);
        _store.SaveVaultCache(new VaultCacheEntry(SchemaVersion, email, serverUrl, cachedAt, json));
    }

    /// <summary>载入缓存并解密进快照(DataStale=true)。校验 schema/email/serverUrl;任何不符/损坏返回 false。</summary>
    public bool TryLoadInto(string email, string serverUrl, SymmetricCryptoKey userKey)
    {
        if (!_store.TryLoadVaultCache(out var entry))
            return false;
        if (entry.SchemaVersion != SchemaVersion
            || !string.Equals(entry.Email, email, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.ServerUrl, serverUrl, StringComparison.Ordinal))
            return false;

        try
        {
            var sync = JsonSerializer.Deserialize(entry.SyncResponseJson, ApiJsonContext.Default.SyncResponse);
            if (sync is null)
                return false;
            var vault = _decryptor.Decrypt(sync, userKey, serverUrl);
            _session.SetSnapshot(vault, dataStale: true);
            _session.SetEncryptedPrivateKey(sync.Profile?.PrivateKey);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            // 缓存损坏/格式不符 → 视为无缓存,走联网。
            return false;
        }
    }

    public void Clear() => _store.ClearVaultCache();
}
