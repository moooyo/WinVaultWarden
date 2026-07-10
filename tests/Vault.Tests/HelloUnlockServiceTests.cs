using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class HelloUnlockServiceTests
{
    private readonly CryptoService _crypto = new();
    private static byte[] Sig(byte b) => Enumerable.Repeat(b, 256).ToArray();
    private static readonly byte[] Challenge = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private (HelloUnlockService svc, MemoryTokenStore store, SymmetricCryptoKey userKey) Unlocked()
    {
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "me@example.com", new ServerOrigin("https://vault.example"));
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, lease.AccountId, lease.Origin, lease.Generation, "me@example.com", "device-id", "refresh",
            "2.protected", KdfType.Pbkdf2, 600_000, null, null));
        session.TryCommit(lease, () =>
        {
            session.SetTokens("access", "refresh");
            session.SetUnlockedKey(userKey);
        });
        return (new HelloUnlockService(_crypto, session, store), store, userKey);
    }

    [Fact]
    public void SetHello_StoresWrappedUserKey_AndChallenge_RoundTrips()
    {
        var (svc, store, userKey) = Unlocked();
        svc.SetHello(Sig(5), Challenge);

        Assert.True(svc.IsHelloSet);
        store.TryLoad(out var p);
        Assert.False(string.IsNullOrEmpty(p.HelloProtectedUserKey));
        Assert.Equal(Convert.ToBase64String(Challenge), p.HelloChallenge);
        Assert.Equal(Challenge, svc.LoadChallenge());

        // 同签名派生密钥应解回同一 UserKey。
        using var helloKey = _crypto.DeriveKeyFromSecret(Sig(5));
        using var back = _crypto.DecryptUserKey(helloKey, EncString.Parse(p.HelloProtectedUserKey!));
        Assert.Equal(userKey.FullKey, back.FullKey);
    }

    [Fact]
    public void WrongSignature_DoesNotDecrypt()
    {
        var (svc, store, _) = Unlocked();
        svc.SetHello(Sig(5), Challenge);
        store.TryLoad(out var p);
        using var wrong = _crypto.DeriveKeyFromSecret(Sig(9));
        Assert.ThrowsAny<Exception>(() => _crypto.DecryptUserKey(wrong, EncString.Parse(p.HelloProtectedUserKey!)));
    }

    [Fact]
    public void ClearHello_RemovesFields()
    {
        var (svc, store, _) = Unlocked();
        svc.SetHello(Sig(5), Challenge);
        svc.ClearHello();
        Assert.False(svc.IsHelloSet);
        Assert.Null(svc.LoadChallenge());
        store.TryLoad(out var p);
        Assert.Null(p.HelloProtectedUserKey);
        Assert.Null(p.HelloChallenge);
    }

    [Fact]
    public void SetHello_WhenLocked_Throws()
    {
        var store = new MemoryTokenStore();
        var session = new VaultSession(); // 未 BeginAccountSession/未解锁 → 锁定
        var lease = session.BeginAccountSession("account-e", "e", new ServerOrigin("https://s.example"));
        store.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, lease.AccountId, lease.Origin, lease.Generation, "e", "d", "r", "2.p", KdfType.Pbkdf2, 600_000, null, null));
        var svc = new HelloUnlockService(_crypto, session, store);
        Assert.ThrowsAny<Exception>(() => svc.SetHello(Sig(1), Challenge));
    }
}
