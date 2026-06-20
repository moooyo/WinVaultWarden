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
