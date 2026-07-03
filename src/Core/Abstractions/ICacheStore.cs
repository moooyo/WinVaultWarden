using Core.Models;

namespace Core.Abstractions;

/// <summary>
/// 加密密码库缓存的持久化。实现须在密文之上再套 DPAPI(CurrentUser),写入原子化。
/// (与 <see cref="ITokenStore"/> 同目录同风格。)
/// </summary>
public interface ICacheStore
{
    void SaveVaultCache(VaultCacheEntry entry);
    bool TryLoadVaultCache(out VaultCacheEntry entry);
    void ClearVaultCache();
}
