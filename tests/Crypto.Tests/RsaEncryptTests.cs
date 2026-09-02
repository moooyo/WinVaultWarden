using System.Security.Cryptography;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class RsaEncryptTests
{
    [Fact]
    public void EncryptRsa_roundtrips_with_DecryptRsa()
    {
        using var rsa = RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfo();
        var priv = rsa.ExportPkcs8PrivateKey();
        var svc = new CryptoService();
        var plain = RandomNumberGenerator.GetBytes(64);
        var enc = svc.EncryptRsa(plain, pub);
        Assert.Equal(4, (int)enc.Type);
        Assert.Equal(plain, svc.DecryptRsa(enc, priv));
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void DecryptRsa_ReadsEveryHistoricalUnsignedSharedKeyType(
        EncryptionType type)
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKey();
        var plaintext = Enumerable.Range(1, 64).Select(index => (byte)index).ToArray();
        var encrypted = EncryptForType(rsa, plaintext, type);

        var decrypted = new CryptoService().DecryptRsa(encrypted, privateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void DecryptRsa_LegacyMacIsNotAnAuthenticationSignal(EncryptionType type)
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportPkcs8PrivateKey();
        var plaintext = Enumerable.Range(1, 64).Select(index => (byte)index).ToArray();
        var encrypted = EncryptForType(rsa, plaintext, type);
        encrypted.Mac![0] ^= 0x80;

        var decrypted = new CryptoService().DecryptRsa(encrypted, privateKey);

        Assert.Equal(plaintext, decrypted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void DecryptRsa_RejectsMalformedLegacyMacLength(int macLength)
    {
        using var rsa = RSA.Create(2048);
        var malformed = new EncString(
            EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64,
            [],
            new byte[256],
            new byte[macLength]);

        Assert.Throws<CryptographicException>(() =>
            new CryptoService().DecryptRsa(malformed, rsa.ExportPkcs8PrivateKey()));
    }

    [Fact]
    public void DecryptRsa_RejectsMacOnTypeWithoutLegacyMacField()
    {
        using var rsa = RSA.Create(2048);
        var malformed = new EncString(
            EncryptionType.Rsa2048_OaepSha1_B64,
            [],
            new byte[256],
            new byte[EncString.LegacyRsaMacLength]);

        Assert.Throws<CryptographicException>(() =>
            new CryptoService().DecryptRsa(malformed, rsa.ExportPkcs8PrivateKey()));
    }

    private static EncString EncryptForType(
        RSA rsa,
        byte[] plaintext,
        EncryptionType type)
    {
        var padding = type is
            EncryptionType.Rsa2048_OaepSha256_B64
                or EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64
            ? RSAEncryptionPadding.OaepSHA256
            : RSAEncryptionPadding.OaepSHA1;
        var mac = type.HasLegacyRsaMac()
            ? Enumerable.Range(1, EncString.LegacyRsaMacLength)
                .Select(index => (byte)index)
                .ToArray()
            : null;
        return new EncString(type, [], rsa.Encrypt(plaintext, padding), mac);
    }
}
