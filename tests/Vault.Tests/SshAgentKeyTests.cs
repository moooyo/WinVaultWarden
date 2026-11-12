using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Xunit;

namespace Vault.Tests;

public sealed class SshAgentKeyTests
{
    [Theory]
    [InlineData(false, 2u, "rsa-sha2-256")]
    [InlineData(false, 4u, "rsa-sha2-512")]
    [InlineData(true, 2u, "rsa-sha2-256")]
    [InlineData(true, 4u, "rsa-sha2-512")]
    public void RsaPemSignsStandardSha2Blobs(bool pkcs8, uint flags, string algorithm)
    {
        using var source = RSA.Create(2048);
        var pem = pkcs8 ? source.ExportPkcs8PrivateKeyPem() : source.ExportRSAPrivateKeyPem();
        using var key = SshAgentKey.Parse(pem);
        var message = "an SSH authentication request"u8.ToArray();
        var signature = ReadSignature(key.Sign(message, flags));
        Assert.Equal(algorithm, signature.Algorithm);
        Assert.True(source.VerifyData(message, signature.Bytes,
            flags == 2 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1));
        Assert.Equal("ssh-rsa", key.Algorithm);
        Assert.StartsWith("SHA256:", key.Fingerprint);
    }

    [Theory]
    [InlineData("nistp256", false)]
    [InlineData("nistp384", false)]
    [InlineData("nistp521", false)]
    [InlineData("nistp256", true)]
    [InlineData("nistp384", true)]
    [InlineData("nistp521", true)]
    public void EcPemSignsStandardMpintBlobs(string curveName, bool pkcs8)
    {
        using var source = ECDsa.Create(Curve(curveName));
        var pem = pkcs8 ? source.ExportPkcs8PrivateKeyPem() : source.ExportECPrivateKeyPem();
        using var key = SshAgentKey.Parse(pem);
        VerifyEcSignature(source, key, curveName);
    }

    [Fact]
    public void OpenSshRsaParsesAndSigns()
    {
        using var source = RSA.Create(2048);
        var parameters = source.ExportParameters(true);
        using var publicBlob = new MemoryStream();
        WriteString(publicBlob, "ssh-rsa"u8);
        WriteMpint(publicBlob, parameters.Exponent!);
        WriteMpint(publicBlob, parameters.Modulus!);
        using var privateFields = new MemoryStream();
        WriteString(privateFields, "ssh-rsa"u8);
        foreach (var field in new[] { parameters.Modulus!, parameters.Exponent!, parameters.D!, parameters.InverseQ!, parameters.P!, parameters.Q! })
            WriteMpint(privateFields, field);
        using var key = SshAgentKey.Parse(OpenSsh(publicBlob.ToArray(), privateFields.ToArray()));
        var message = "rsa SSH request"u8.ToArray();
        var signature = ReadSignature(key.Sign(message, SshAgentKey.RsaSha512Flag));
        Assert.True(source.VerifyData(message, signature.Bytes, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1));
        Assert.Equal(publicBlob.ToArray(), key.PublicKeyBlob);
    }

    [Theory]
    [InlineData("nistp256")]
    [InlineData("nistp384")]
    [InlineData("nistp521")]
    public void OpenSshEcParsesAndSigns(string curveName)
    {
        using var source = ECDsa.Create(Curve(curveName));
        var parameters = source.ExportParameters(true);
        var point = new byte[1 + parameters.Q.X!.Length + parameters.Q.Y!.Length];
        point[0] = 4;
        parameters.Q.X.CopyTo(point, 1);
        parameters.Q.Y.CopyTo(point, 1 + parameters.Q.X.Length);
        using var publicBlob = new MemoryStream();
        WriteString(publicBlob, Encoding.ASCII.GetBytes("ecdsa-sha2-" + curveName));
        WriteString(publicBlob, Encoding.ASCII.GetBytes(curveName));
        WriteString(publicBlob, point);
        using var privateFields = new MemoryStream();
        privateFields.Write(publicBlob.ToArray());
        WriteMpint(privateFields, parameters.D!);
        using var key = SshAgentKey.Parse(OpenSsh(publicBlob.ToArray(), privateFields.ToArray()));
        VerifyEcSignature(source, key, curveName);
        Assert.Equal(publicBlob.ToArray(), key.PublicKeyBlob);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ed25519ParsesAndSigns(bool pkcs8)
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var publicKey = new byte[32];
        Ed25519.GeneratePublicKey(seed, publicKey);
        var pem = pkcs8 ? Ed25519Pkcs8(seed) : OpenSshEd25519(seed, publicKey);
        using var key = SshAgentKey.Parse(pem);
        var message = "ed25519 SSH request"u8.ToArray();
        var signature = ReadSignature(key.Sign(message, 0));
        Assert.Equal("ssh-ed25519", signature.Algorithm);
        Assert.True(Ed25519.Verify(signature.Bytes, publicKey, message));
        Assert.Equal(64, signature.Bytes.Length);
        CryptographicOperations.ZeroMemory(seed);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(6u)]
    [InlineData(uint.MaxValue)]
    public void RsaRejectsSha1AndUnknownFlags(uint flags)
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportRSAPrivateKeyPem());
        Assert.Throws<CryptographicException>(() => key.Sign("request"u8, flags));
    }

    [Fact]
    public void Ed25519RejectsFlagsAndDisposedKey()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var key = SshAgentKey.Parse(Ed25519Pkcs8(seed));
        Assert.Throws<CryptographicException>(() => key.Sign("request"u8, 2));
        key.Dispose();
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => key.Sign("request"u8, 0));
        CryptographicOperations.ZeroMemory(seed);
    }

    [Fact]
    public void RejectsMismatchedStoredPublicKey()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        using var secondKey = SshAgentKey.Parse(second.ExportRSAPrivateKeyPem());
        var expected = "ssh-rsa " + Convert.ToBase64String(secondKey.PublicKeyBlob) + " comment";
        Assert.Throws<CryptographicException>(() => SshAgentKey.Parse(first.ExportRSAPrivateKeyPem(), expected));
        using var correct = SshAgentKey.Parse(second.ExportRSAPrivateKeyPem(), expected);
        Assert.Equal(secondKey.Fingerprint, correct.Fingerprint);
    }

    [Fact]
    public void RejectsOpenSshPublicKeyMismatch()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var publicKey = new byte[32];
        Ed25519.GeneratePublicKey(seed, publicKey);
        publicKey[0] ^= 1;
        Assert.Throws<CryptographicException>(() => SshAgentKey.Parse(OpenSshEd25519(seed, publicKey)));
        CryptographicOperations.ZeroMemory(seed);
    }

    [Fact]
    public void RejectsMultipleKeysEncryptedKeysAndExcessiveInput()
    {
        using var source = RSA.Create(2048);
        var pem = source.ExportRSAPrivateKeyPem();
        Assert.Throws<CryptographicException>(() => SshAgentKey.Parse(pem + pem));
        var encrypted = source.ExportEncryptedPkcs8PrivateKeyPem("test-only", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));
        Assert.Throws<CryptographicException>(() => SshAgentKey.Parse(encrypted));
        Assert.Throws<CryptographicException>(() => SshAgentKey.Parse(new string('x', SshAgentKey.MaxPrivateKeyCharacters + 1)));
        using var key = SshAgentKey.Parse(pem);
        Assert.Throws<CryptographicException>(() => key.Sign(new byte[SshAgentKey.MaxSignedDataBytes + 1], 2));
    }

    [Fact]
    public void PublicBlobIsIndependentOfCallerMutation()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportRSAPrivateKeyPem());
        var original = key.PublicKeyBlob;
        var changed = key.PublicKeyBlob;
        changed[0] ^= 0x7f;
        Assert.Equal(original, key.PublicKeyBlob);
    }

    [Theory]
    [InlineData("cipher")]
    [InlineData("kdf")]
    [InlineData("multiple")]
    [InlineData("checks")]
    [InlineData("padding")]
    [InlineData("public")]
    public void RejectsMalformedOpenSshContainer(string defect)
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var publicKey = new byte[32];
        Ed25519.GeneratePublicKey(seed, publicKey);
        using var publicBlob = new MemoryStream();
        WriteString(publicBlob, "ssh-ed25519"u8);
        WriteString(publicBlob, publicKey);
        using var privateFields = new MemoryStream();
        privateFields.Write(publicBlob.ToArray());
        var extended = new byte[64];
        seed.CopyTo(extended, 0);
        publicKey.CopyTo(extended, 32);
        WriteString(privateFields, extended);
        var advertised = publicBlob.ToArray();
        if (defect == "public") advertised[^1] ^= 1;
        var pem = OpenSsh(advertised, privateFields.ToArray(),
            cipher: defect == "cipher" ? "aes256-ctr" : "none",
            kdf: defect == "kdf" ? "bcrypt" : "none",
            count: defect == "multiple" ? 2u : 1u,
            unequalChecks: defect == "checks",
            badPadding: defect == "padding");
        Assert.Throws<CryptographicException>(() => SshAgentKey.Parse(pem));
        CryptographicOperations.ZeroMemory(seed);
        CryptographicOperations.ZeroMemory(extended);
    }

    private static void VerifyEcSignature(ECDsa source, SshAgentKey key, string curveName)
    {
        var message = "ecdsa SSH request"u8.ToArray();
        var signature = ReadSignature(key.Sign(message, 0));
        Assert.Equal("ecdsa-sha2-" + curveName, signature.Algorithm);
        var offset = 0;
        var r = ReadString(signature.Bytes, ref offset);
        var s = ReadString(signature.Bytes, ref offset);
        Assert.Equal(signature.Bytes.Length, offset);
        var size = (source.KeySize + 7) / 8;
        var p1363 = new byte[size * 2];
        while (r.Length > 0 && r[0] == 0) r = r[1..];
        while (s.Length > 0 && s[0] == 0) s = s[1..];
        r.CopyTo(p1363.AsSpan(size - r.Length));
        s.CopyTo(p1363.AsSpan(size * 2 - s.Length));
        var hash = curveName switch { "nistp256" => HashAlgorithmName.SHA256, "nistp384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
        Assert.True(source.VerifyData(message, p1363, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Throws<CryptographicException>(() => key.Sign(message, 1));
    }

    private static (string Algorithm, byte[] Bytes) ReadSignature(byte[] signature)
    {
        var offset = 0;
        var algorithm = Encoding.ASCII.GetString(ReadString(signature, ref offset));
        var bytes = ReadString(signature, ref offset).ToArray();
        Assert.Equal(signature.Length, offset);
        return (algorithm, bytes);
    }

    private static ReadOnlySpan<byte> ReadString(ReadOnlySpan<byte> value, ref int offset)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(value[offset..]));
        offset += 4;
        var result = value.Slice(offset, length);
        offset += length;
        return result;
    }

    private static ECCurve Curve(string name) => name switch
    {
        "nistp256" => ECCurve.NamedCurves.nistP256,
        "nistp384" => ECCurve.NamedCurves.nistP384,
        _ => ECCurve.NamedCurves.nistP521,
    };

    private static string Ed25519Pkcs8(byte[] seed) => PemEncoding.WriteString("PRIVATE KEY",
        new PrivateKeyInfo(new AlgorithmIdentifier(new DerObjectIdentifier("1.3.101.112")), new DerOctetString(seed)).GetEncoded());

    private static string OpenSshEd25519(byte[] seed, byte[] publicKey)
    {
        using var publicBlob = new MemoryStream();
        WriteString(publicBlob, "ssh-ed25519"u8);
        WriteString(publicBlob, publicKey);
        using var privateFields = new MemoryStream();
        WriteString(privateFields, "ssh-ed25519"u8);
        WriteString(privateFields, publicKey);
        var extended = new byte[64];
        seed.CopyTo(extended, 0);
        publicKey.CopyTo(extended, 32);
        WriteString(privateFields, extended);
        CryptographicOperations.ZeroMemory(extended);
        return OpenSsh(publicBlob.ToArray(), privateFields.ToArray());
    }

    private static string OpenSsh(byte[] publicBlob, byte[] privateFields,
        string cipher = "none", string kdf = "none", uint count = 1,
        bool unequalChecks = false, bool badPadding = false)
    {
        using var inner = new MemoryStream();
        WriteUInt32(inner, 123456789);
        WriteUInt32(inner, unequalChecks ? 123456780u : 123456789u);
        inner.Write(privateFields);
        WriteString(inner, "test key"u8);
        var padding = (8 - inner.Length % 8) % 8;
        for (byte index = 1; index <= padding; index++) inner.WriteByte(badPadding ? (byte)0 : index);
        using var outer = new MemoryStream();
        outer.Write("openssh-key-v1\0"u8);
        WriteString(outer, Encoding.ASCII.GetBytes(cipher));
        WriteString(outer, Encoding.ASCII.GetBytes(kdf));
        WriteString(outer, []);
        WriteUInt32(outer, count);
        WriteString(outer, publicBlob);
        WriteString(outer, inner.ToArray());
        return PemEncoding.WriteString("OPENSSH PRIVATE KEY", outer.ToArray());
    }

    private static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> prefix = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, value);
        stream.Write(prefix);
    }

    private static void WriteMpint(Stream stream, ReadOnlySpan<byte> value)
    {
        while (!value.IsEmpty && value[0] == 0) value = value[1..];
        var zero = !value.IsEmpty && (value[0] & 0x80) != 0;
        WriteUInt32(stream, checked((uint)value.Length + (zero ? 1u : 0u)));
        if (zero) stream.WriteByte(0);
        stream.Write(value);
    }
}
