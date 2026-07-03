using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class EncryptDecryptTests
{
    private static SymmetricCryptoKey NewKey() => new(RandomNumberGenerator.GetBytes(64));

    [Fact]
    public void EncryptThenDecrypt_RoundTripsPlaintext()
    {
        var svc = new CryptoService();
        var key = NewKey();
        var plaintext = Encoding.UTF8.GetBytes("hello vault");

        var enc = svc.Encrypt(plaintext, key);
        var dec = svc.Decrypt(enc, key);

        Assert.Equal(plaintext, dec);
        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, enc.Type);
    }

    [Fact]
    public void Decrypt_TamperedMac_Throws()
    {
        var svc = new CryptoService();
        var key = NewKey();
        var enc = svc.Encrypt(Encoding.UTF8.GetBytes("secret"), key);

        // 篡改 MAC 一字节
        enc.Mac![0] ^= 0xFF;

        Assert.Throws<CryptographicException>(() => svc.Decrypt(enc, key));
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var svc = new CryptoService();
        var enc = svc.Encrypt(Encoding.UTF8.GetBytes("secret"), NewKey());

        Assert.Throws<CryptographicException>(() => svc.Decrypt(enc, NewKey()));
    }

    [Fact]
    public void Decrypt_MacCapableKey_RejectsStrippedNoMacCiphertext_DowngradeGuard()
    {
        // C1: 恶意/MITM 服务端把认证的 type-2 字段剥成无 MAC 的 type-0。
        // MAC-capable(64 字节)密钥必须拒绝,否则击穿 enc-then-MAC。
        var svc = new CryptoService();
        var key = NewKey();
        var enc = svc.Encrypt(Encoding.UTF8.GetBytes("secret"), key); // type-2 带 MAC

        var stripped = new EncString(EncryptionType.AesCbc256_B64, enc.Iv, enc.Ct, null);

        var ex = Assert.Throws<CryptographicException>(() => svc.Decrypt(stripped, key));
        Assert.Contains("拒绝降级", ex.Message);
    }

    [Fact]
    public void Decrypt_NonMacKey_StillDecryptsOwnNoMacCiphertext()
    {
        // 守卫只针对 MAC-capable 密钥;32 字节无 MAC 密钥解密自身 type-0 输出仍应正常(不误拒)。
        var svc = new CryptoService();
        var key = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32));
        Assert.Null(key.MacKey);
        var enc = svc.Encrypt(Encoding.UTF8.GetBytes("plain"), key);
        Assert.Null(enc.Mac);

        Assert.Equal(Encoding.UTF8.GetBytes("plain"), svc.Decrypt(enc, key));
    }

    [Fact]
    public void DecryptRsa_RoundTripsThroughRsaOaepSha1()
    {
        var svc = new CryptoService();
        using var rsa = RSA.Create(2048);
        var plaintext = Encoding.UTF8.GetBytes("user key material");

        // 模拟服务端:用公钥 RSA-OAEP-SHA1 加密(type 4)
        var ctBytes = rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA1);
        var enc = new EncString(EncryptionType.Rsa2048_OaepSha1_B64, Array.Empty<byte>(), ctBytes, null);
        var privateKeyDer = rsa.ExportPkcs8PrivateKey();

        var dec = svc.DecryptRsa(enc, privateKeyDer);

        Assert.Equal(plaintext, dec);
    }
}
