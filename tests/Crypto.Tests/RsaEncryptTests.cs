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
}
