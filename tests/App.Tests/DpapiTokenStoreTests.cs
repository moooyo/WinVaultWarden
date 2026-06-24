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
}
