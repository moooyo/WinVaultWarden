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
        Assert.Null(session.AccessToken);
        Assert.Null(session.RefreshToken);
        Assert.Null(session.UserKey);
        Assert.Empty(session.Ciphers);
        Assert.Empty(session.Folders);
        Assert.Empty(session.Devices);
        Assert.All(key.FullKey, b => Assert.Equal(0, b));
        Assert.All(key.EncKey, b => Assert.Equal(0, b));
        Assert.NotNull(key.MacKey);
        Assert.All(key.MacKey!, b => Assert.Equal(0, b));
        Assert.Equal("me@example.com", session.Account.Email);

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

    [Fact]
    public void SetSnapshot_ExposesSkippedCipherCount_ResetOnLockAndClear()
    {
        var session = new VaultSession();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var account = new AccountInfo("me@example.com", "https://vault.example", "M", "PBKDF2 600000");

        session.SetUnlockedKey(key);
        session.SetSnapshot(new DecryptedVault(account, [], [], SkippedCipherCount: 3));
        Assert.Equal(3, session.SkippedCipherCount);

        session.Lock();
        Assert.Equal(0, session.SkippedCipherCount);

        // 重新解锁后才能再次写入快照。
        session.SetUnlockedKey(key);
        session.SetSnapshot(new DecryptedVault(account, [], [], SkippedCipherCount: 5));
        Assert.Equal(5, session.SkippedCipherCount);

        session.Clear();
        Assert.Equal(0, session.SkippedCipherCount);
    }

    [Fact]
    public void SetSnapshot_AfterLock_IsDropped_NoPlaintextResurrection()
    {
        // S6:并发锁定时,在途同步完成后的 SetSnapshot 不得把明文条目回灌进已锁定会话。
        var session = new VaultSession();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var account = new AccountInfo("me@example.com", "https://vault.example", "M", "PBKDF2 600000");
        var ciphers = new[] { new Cipher { Id = "c1", Type = Core.Enums.CipherType.Login, Name = "Secret" } };

        session.SetUnlockedKey(key);
        session.Lock(); // 模拟同步在途期间被并发锁定

        session.SetSnapshot(new DecryptedVault(account, [], ciphers, SkippedCipherCount: 2));

        Assert.Equal(Core.Session.VaultState.Locked, session.State);
        Assert.Empty(session.Ciphers);          // 明文未回灌
        Assert.Equal(0, session.SkippedCipherCount);
    }

    [Fact]
    public void MarkUnlockedIfKeyPresent_OnlyUnlocksWhenKeyStillPresent()
    {
        // S8:并发锁定后 bootstrap 结尾不得把状态强行改回 Unlocked。
        var session = new VaultSession();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

        // 有 UserKey → 迁移到 Unlocked。
        session.SetUnlockedKey(key);
        session.SetState(Core.Session.VaultState.Syncing);
        session.MarkUnlockedIfKeyPresent();
        Assert.Equal(Core.Session.VaultState.Unlocked, session.State);

        // 已锁定(UserKey 为空)→ 保持 Locked,不被强行 Unlocked。
        session.Lock();
        session.MarkUnlockedIfKeyPresent();
        Assert.Equal(Core.Session.VaultState.Locked, session.State);
    }

    [Fact]
    public void SetSnapshot_DataStaleFlag_ExposedAndResetOnLock()
    {
        var session = new VaultSession();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var account = new AccountInfo("me@example.com", "https://vault.example", "M", "PBKDF2 600000");

        session.SetUnlockedKey(key);
        session.SetSnapshot(new DecryptedVault(account, [], [], SkippedCipherCount: 0), dataStale: true);
        Assert.True(session.DataStale);

        session.SetUnlockedKey(key);
        session.SetSnapshot(new DecryptedVault(account, [], [], SkippedCipherCount: 0), dataStale: false);
        Assert.False(session.DataStale);

        session.SetUnlockedKey(key);
        session.SetSnapshot(new DecryptedVault(account, [], [], SkippedCipherCount: 0), dataStale: true);
        session.Lock();
        Assert.False(session.DataStale);
    }
}
