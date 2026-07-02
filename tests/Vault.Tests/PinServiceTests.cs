using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class PinServiceTests
{
    private readonly CryptoService _crypto = new();
    private const string Email = "me@example.com";
    private const int Iter = 600_000;

    private (PinService pin, VaultSession session, MemoryTokenStore store, SymmetricCryptoKey userKey) Unlocked()
    {
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession("https://vault.example", Email, "device-id", "refresh",
            "2.protected", KdfType.Pbkdf2, Iter, null, null));
        var session = new VaultSession();
        session.SetTokens("access", "refresh");
        session.SetUnlockedKey(userKey);
        return (new PinService(_crypto, session, store), session, store, userKey);
    }

    [Fact]
    public void SetPin_StoresPinProtectedUserKey_RoundTripsToSameUserKey()
    {
        var (pin, _, store, userKey) = Unlocked();
        pin.SetPin("1234");

        Assert.True(pin.IsPinSet);
        store.TryLoad(out var p);
        Assert.False(string.IsNullOrEmpty(p.PinProtectedUserKey));
        Assert.Equal(0, p.PinFailedAttempts);

        // 用 "1234" 派生密钥应解回同一 UserKey。
        var pinKey = _crypto.DeriveMasterKey("1234", Email, KdfType.Pbkdf2, Iter, null, null);
        var stretched = _crypto.StretchMasterKey(pinKey);
        var decrypted = _crypto.DecryptUserKey(stretched, EncString.Parse(p.PinProtectedUserKey!));
        Assert.Equal(userKey.FullKey, decrypted.FullKey);
    }

    [Fact]
    public void WrongPin_DoesNotDecrypt()
    {
        var (pin, _, store, _) = Unlocked();
        pin.SetPin("1234");
        store.TryLoad(out var p);
        var pinKey = _crypto.DeriveMasterKey("9999", Email, KdfType.Pbkdf2, Iter, null, null);
        var stretched = _crypto.StretchMasterKey(pinKey);
        Assert.ThrowsAny<Exception>(() => _crypto.DecryptUserKey(stretched, EncString.Parse(p.PinProtectedUserKey!)));
    }

    [Fact]
    public void ClearPin_RemovesKey()
    {
        var (pin, _, _, _) = Unlocked();
        pin.SetPin("1234");
        pin.ClearPin();
        Assert.False(pin.IsPinSet);
    }

    [Fact]
    public void SetPin_WhenLocked_Throws()
    {
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession("https://vault.example", Email, "device-id", "refresh",
            "2.protected", KdfType.Pbkdf2, Iter, null, null));
        var session = new VaultSession();   // 未 SetUnlockedKey → 锁定
        var pin = new PinService(_crypto, session, store);
        Assert.Throws<InvalidOperationException>(() => pin.SetPin("1234"));
    }
}
