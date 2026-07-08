using Crypto;
using Xunit;

namespace Crypto.Tests;

public class DeriveKeyFromSecretTests
{
    private readonly CryptoService _crypto = new();
    private static byte[] Sig(byte b) => Enumerable.Repeat(b, 256).ToArray(); // RSA-2048 sig 长度

    [Fact]
    public void DeriveKeyFromSecret_IsDeterministic_For_SameSecret()
    {
        using var k1 = _crypto.DeriveKeyFromSecret(Sig(7));
        using var k2 = _crypto.DeriveKeyFromSecret(Sig(7));
        Assert.Equal(k1.FullKey, k2.FullKey);
        Assert.Equal(64, k1.FullKey.Length);
    }

    [Fact]
    public void DeriveKeyFromSecret_EncAndMac_Differ_And_VaryBySecret()
    {
        using var k = _crypto.DeriveKeyFromSecret(Sig(7));
        Assert.NotEqual(k.EncKey, k.MacKey);
        using var other = _crypto.DeriveKeyFromSecret(Sig(9));
        Assert.NotEqual(k.FullKey, other.FullKey);
    }

    [Fact]
    public void DeriveKeyFromSecret_WrapsAndUnwraps_UserKey()
    {
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        using var helloKey = _crypto.DeriveKeyFromSecret(Sig(3));
        var enc = _crypto.Encrypt(userKey.FullKey, helloKey);
        using var back = _crypto.DecryptUserKey(_crypto.DeriveKeyFromSecret(Sig(3)), enc);
        Assert.Equal(userKey.FullKey, back.FullKey);
    }
}
