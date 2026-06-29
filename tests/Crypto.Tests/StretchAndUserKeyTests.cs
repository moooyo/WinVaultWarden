using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class StretchAndUserKeyTests
{
    [Fact]
    public void StretchMasterKey_MatchesHkdfExpandReference()
    {
        var svc = new CryptoService();
        var masterKey = svc.DeriveMasterKey("p4ssw0rd", "nobody@example.com", KdfType.Pbkdf2, 600_000, null, null);

        var stretched = svc.StretchMasterKey(masterKey);

        // 参考:两次独立 HKDF-Expand(SHA256),info="enc"/"mac",各 32 字节
        var enc = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, Encoding.UTF8.GetBytes("enc"));
        var mac = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, Encoding.UTF8.GetBytes("mac"));

        Assert.Equal(enc, stretched.EncKey);
        Assert.Equal(mac, stretched.MacKey);
        Assert.Equal(64, stretched.FullKey.Length);
    }

    [Fact]
    public void DecryptUserKey_RoundTripsThroughEncrypt()
    {
        var svc = new CryptoService();
        var masterKey = svc.DeriveMasterKey("p4ssw0rd", "nobody@example.com", KdfType.Pbkdf2, 600_000, null, null);
        var stretched = svc.StretchMasterKey(masterKey);

        // 模拟服务端:用 stretched key 加密一把随机 64 字节 UserKey
        var userKeyPlain = RandomNumberGenerator.GetBytes(64);
        var protectedUserKey = svc.Encrypt(userKeyPlain, stretched);

        var decrypted = svc.DecryptUserKey(stretched, protectedUserKey);

        Assert.Equal(userKeyPlain, decrypted.FullKey);
        Assert.Equal(32, decrypted.EncKey.Length);
        Assert.Equal(32, decrypted.MacKey!.Length);
    }

    [Fact]
    public void ProtectUserKey_roundtrips_with_DecryptUserKey()
    {
        var svc = new CryptoService();
        var mk = svc.DeriveMasterKey("pw", "a@b.com", KdfType.Pbkdf2, 600000, null, null);
        var userKey = new SymmetricCryptoKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var protectedKey = svc.ProtectUserKey(mk, userKey);
        var restored = svc.DecryptUserKey(svc.StretchMasterKey(mk), protectedKey);
        Assert.Equal(userKey.FullKey, restored.FullKey);
        Assert.Equal(2, (int)protectedKey.Type); // AesCbc256_HmacSha256
    }
}
