using Core.Abstractions;
using Core.Models;

namespace Vault.Tests;

public sealed class FakeCacheStore : ICacheStore
{
    private VaultCacheEntry? _entry;
    public int SaveCount { get; private set; }
    public int ClearCount { get; private set; }
    public VaultCacheEntry? LastSaved { get; private set; }
    public Action? BeforeSave { get; set; }
    public Action? BeforeLoad { get; set; }
    public Action? BeforeClear { get; set; }
    public List<string> Operations { get; } = [];

    public void SaveVaultCache(VaultCacheEntry entry)
    {
        BeforeSave?.Invoke();
        _entry = entry;
        LastSaved = entry;
        SaveCount++;
        Operations.Add("save");
    }

    public bool TryLoadVaultCache(out VaultCacheEntry entry)
    {
        BeforeLoad?.Invoke();
        entry = _entry!;
        return _entry is not null;
    }

    public void ClearVaultCache()
    {
        BeforeClear?.Invoke();
        _entry = null;
        LastSaved = null;
        ClearCount++;
        Operations.Add("clear");
    }
}
