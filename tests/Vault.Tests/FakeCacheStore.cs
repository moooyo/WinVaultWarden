using Core.Abstractions;
using Core.Models;

namespace Vault.Tests;

public sealed class FakeCacheStore : ICacheStore
{
    private VaultCacheEntry? _entry;
    public int SaveCount { get; private set; }
    public int ClearCount { get; private set; }
    public void SaveVaultCache(VaultCacheEntry entry) { _entry = entry; SaveCount++; }
    public bool TryLoadVaultCache(out VaultCacheEntry entry) { entry = _entry!; return _entry is not null; }
    public void ClearVaultCache() { _entry = null; ClearCount++; }
}
