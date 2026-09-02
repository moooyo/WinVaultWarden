using Core.Models;

namespace Core.Abstractions;

public enum VaultCacheRemovalStatus
{
    Removed,
    Absent,
    Corrupt,
    Unavailable,
}

/// <summary>
/// 加密密码库缓存的持久化。实现须在密文之上再套 DPAPI(CurrentUser),写入原子化。
/// (与 <see cref="ITokenStore"/> 同目录同风格。)
/// </summary>
public interface ICacheStore
{
    void SaveVaultCache(VaultCacheEntry entry);
    bool TryLoadVaultCache(out VaultCacheEntry entry);
    void ClearVaultCache();

    bool TryLoadVaultCache(
        string accountId,
        ServerEndpoints endpoints,
        out VaultCacheEntry entry)
    {
        if (TryLoadVaultCache(out entry)
            && entry.AccountId == accountId
            && (entry.Endpoints == endpoints
                || (entry.Endpoints is null
                    && entry.Origin == endpoints.WebVault.Origin)))
            return true;

        entry = null!;
        return false;
    }

    void ClearVaultCache(string accountId, ServerEndpoints endpoints) =>
        ClearVaultCache();

    /// <summary>
    /// Removes one exact account cache while distinguishing absence from unreadable storage.
    /// The default is fail-closed because the legacy void method cannot report its outcome.
    /// </summary>
    VaultCacheRemovalStatus RemoveVaultCacheExact(
        string accountId,
        ServerEndpoints endpoints) => VaultCacheRemovalStatus.Unavailable;
}
