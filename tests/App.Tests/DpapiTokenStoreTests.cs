using App.Services;
using Core.Enums;
using Core.Models;
using Xunit;

namespace App.Tests;

public class DpapiTokenStoreTests
{
    [Fact]
    public void SaveLoadAndClear_RoundTripsWithoutPlaintextJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        var session = new PersistedSession(
            "https://vault.example",
            "me@example.com",
            "device-id",
            "refresh-token",
            "protected-user-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);

        store.Save(session);

        var fileText = File.ReadAllText(path);
        Assert.DoesNotContain("refresh-token", fileText);
        Assert.DoesNotContain("protected-user-key", fileText);
        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal(session, loaded);

        store.Clear();

        Assert.False(File.Exists(path));
        Assert.False(store.TryLoad(out _));
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempAndReplacesExisting()
    {
        // R9:原子写(temp+move)——保存后不残留 .tmp,且再次保存原子替换、可正确读回最新值。
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        var first = new PersistedSession(
            "https://vault.example", "me@example.com", "device-id",
            "refresh-1", "puk", KdfType.Pbkdf2, 600_000, null, null);

        store.Save(first);
        store.Save(first with { RefreshToken = "refresh-2" }); // 覆盖

        Assert.False(File.Exists(path + ".tmp"), "临时文件应已被 Move,不应残留");
        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal("refresh-2", loaded.RefreshToken);
    }

    [Fact]
    public void SaveLoad_RoundTripsTwoFactorToken_Encrypted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        var session = new PersistedSession(
            "https://vault.example", "me@example.com", "device-id",
            "refresh", "puk", KdfType.Pbkdf2, 600_000, null, null)
        {
            TwoFactorToken = "remember-jwt",
        };

        store.Save(session);

        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal("remember-jwt", loaded.TwoFactorToken);
        Assert.DoesNotContain("remember-jwt", File.ReadAllText(path)); // DPAPI 加密,不留明文
    }
}
