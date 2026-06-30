using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultSessionPrivateKeyTests
{
    [Fact]
    public void SetEncryptedPrivateKey_RoundTrips()
    {
        var s = new VaultSession();
        s.SetEncryptedPrivateKey("2.abc|def|ghi");
        Assert.Equal("2.abc|def|ghi", s.EncryptedPrivateKey);
    }

    [Fact]
    public void Lock_ClearsEncryptedPrivateKey()
    {
        var s = new VaultSession();
        s.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        s.SetEncryptedPrivateKey("2.abc|def|ghi");
        s.Lock();
        Assert.Null(s.EncryptedPrivateKey);
    }
}
