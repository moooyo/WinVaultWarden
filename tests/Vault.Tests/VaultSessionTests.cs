using Core.Enums;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultSessionTests
{
    [Fact]
    public void LockAndClear_UpdateStateAndInMemorySecrets()
    {
        var session = new VaultSession();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var cipher = new Cipher { Id = "c1", Type = CipherType.Login, Name = "GitHub" };
        var folder = new Folder { Id = "f1", Name = "Work" };
        var device = new DeviceInfo("d1", "Desktop", 6, "d1", null, false);

        session.SetTokens("access", "refresh");
        session.SetUnlockedKey(key);
        session.SetSnapshot(new DecryptedVault(
            new AccountInfo("me@example.com", "https://vault.example", "M", "PBKDF2 600000"),
            [folder],
            [cipher],
            0));
        session.SetDevices([device]);

        Assert.Equal(VaultState.Unlocked, session.State);
        Assert.Same(key, session.UserKey);
        Assert.Equal("access", session.AccessToken);
        Assert.Single(session.Ciphers);
        Assert.Single(session.Folders);
        Assert.Single(session.Devices);

        session.Lock();

        Assert.Equal(VaultState.Locked, session.State);
        Assert.Null(session.UserKey);
        Assert.Single(session.Ciphers);

        session.Clear();

        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
        Assert.Null(session.UserKey);
        Assert.Empty(session.Ciphers);
        Assert.Empty(session.Folders);
        Assert.Empty(session.Devices);
        Assert.Equal(AccountInfo.Empty, session.Account);
    }
}
