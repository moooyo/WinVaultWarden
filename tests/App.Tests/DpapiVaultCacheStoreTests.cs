using App.Services;
using Core.Models;
using Xunit;

namespace App.Tests;

public class DpapiVaultCacheStoreTests
{
    [Fact]
    public void SaveLoadClear_RoundTrips_AndProtectsPayload()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "vault-cache.bin");
        var store = new DpapiVaultCacheStore(path);
        var entry = new VaultCacheEntry(
            2,
            "account-1",
            new ServerOrigin("https://vault.example"),
            7,
            "me@example.com",
            DateTimeOffset.UnixEpoch,
            "{\"secret-cipher-json\":\"2.abc\"}");

        store.SaveVaultCache(entry);

        // DPAPI 保护:文件不应含明文 payload。
        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("secret-cipher-json", raw);
        Assert.False(File.Exists(path + ".tmp")); // 原子写不留 tmp

        Assert.True(store.TryLoadVaultCache(out var loaded));
        Assert.Equal(entry, loaded);

        store.ClearVaultCache();
        Assert.False(store.TryLoadVaultCache(out _));
    }

    [Fact]
    public void TryLoad_WhenReadThrowsIOException_ReturnsFalse()
    {
        AssertReadFailureReturnsFalse(new IOException("read failed"));
    }

    [Fact]
    public void TryLoad_WhenReadThrowsUnauthorizedAccess_ReturnsFalse()
    {
        AssertReadFailureReturnsFalse(
            new UnauthorizedAccessException("read denied"));
    }

    private static void AssertReadFailureReturnsFalse(Exception readFailure)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "vault-cache.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, [0]);
            Func<string, byte[]> reader = _ => throw readFailure;
            var store = new DpapiVaultCacheStore(path, reader);

            Assert.False(store.TryLoadVaultCache(out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
