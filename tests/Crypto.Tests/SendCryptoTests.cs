using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class SendCryptoTests
{
    private static SymmetricCryptoKey NewUserKey() => new(RandomNumberGenerator.GetBytes(64));
    private static SendCryptoService NewService() => new(new CryptoService());

    [Fact]
    public void GenerateSeed_Returns16RandomBytes()
    {
        var svc = NewService();

        var a = svc.GenerateSeed();
        var b = svc.GenerateSeed();

        Assert.Equal(16, a.Length);
        Assert.Equal(16, b.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveCryptoKey_IsDeterministic_AndSplits64Into32Enc32Mac()
    {
        var svc = NewService();
        var seed = svc.GenerateSeed();

        var k1 = svc.DeriveCryptoKey(seed);
        var k2 = svc.DeriveCryptoKey(seed);

        Assert.Equal(64, k1.FullKey.Length);
        Assert.Equal(32, k1.EncKey.Length);
        Assert.NotNull(k1.MacKey);
        Assert.Equal(32, k1.MacKey!.Length);
        // 确定性:同 seed 派生相同 key
        Assert.Equal(k1.FullKey, k2.FullKey);

        // 参考:完整 HKDF(extract+expand)SHA256,salt="bitwarden-send",info="send",64B
        var expected = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: seed,
            outputLength: 64,
            salt: "bitwarden-send"u8.ToArray(),
            info: "send"u8.ToArray());
        Assert.Equal(expected, k1.FullKey);
    }

    [Fact]
    public void DeriveCryptoKey_DifferentSeeds_ProduceDifferentKeys()
    {
        var svc = NewService();

        var k1 = svc.DeriveCryptoKey(svc.GenerateSeed());
        var k2 = svc.DeriveCryptoKey(svc.GenerateSeed());

        Assert.NotEqual(k1.FullKey, k2.FullKey);
    }

    [Fact]
    public void WrapSeed_UnwrapSeed_RoundTripsUnderUserKey()
    {
        var svc = NewService();
        var userKey = NewUserKey();
        var seed = svc.GenerateSeed();

        var wrapped = svc.WrapSeed(seed, userKey);
        var unwrapped = svc.UnwrapSeed(wrapped, userKey);

        Assert.Equal(seed, unwrapped);
        // 用户密钥有 MacKey,key 字段应为 type 2(AesCbc256_HmacSha256)
        Assert.StartsWith("2.", wrapped);
    }

    [Fact]
    public void EncryptField_DecryptField_RoundTripsUnderCryptoKey()
    {
        var svc = NewService();
        var cryptoKey = svc.DeriveCryptoKey(svc.GenerateSeed());

        var enc = svc.EncryptField("hello send", cryptoKey);
        var dec = svc.DecryptField(enc, cryptoKey);

        Assert.Equal("hello send", dec);
        Assert.StartsWith("2.", enc);
    }

    [Fact]
    public void DecryptField_NullOrEmpty_ReturnsNull()
    {
        var svc = NewService();
        var cryptoKey = svc.DeriveCryptoKey(svc.GenerateSeed());

        Assert.Null(svc.DecryptField(null, cryptoKey));
        Assert.Null(svc.DecryptField("", cryptoKey));
    }

    [Fact]
    public void EncryptToBuffer_DecryptBuffer_RoundTrips_WithCorrectLayout()
    {
        var svc = NewService();
        var cryptoKey = svc.DeriveCryptoKey(svc.GenerateSeed());
        var plaintext = Encoding.UTF8.GetBytes("file contents to encrypt as EncArrayBuffer");

        var buffer = svc.EncryptToBuffer(plaintext, cryptoKey);

        // 布局:byte0 = 2 (AesCbc256_HmacSha256),iv(16),mac(32),ct(...)
        Assert.Equal(2, buffer[0]);
        var ctLen = buffer.Length - 1 - 16 - 32;
        Assert.True(ctLen > 0);
        Assert.Equal(1 + 16 + 32 + ctLen, buffer.Length);

        var dec = svc.DecryptBuffer(buffer, cryptoKey);
        Assert.Equal(plaintext, dec);
    }

    [Fact]
    public void ComputePasswordProof_IsDeterministic_44CharBase64Of32Bytes()
    {
        var svc = NewService();
        var seed = svc.GenerateSeed();

        var p1 = svc.ComputePasswordProof("p4ssw0rd", seed);
        var p2 = svc.ComputePasswordProof("p4ssw0rd", seed);

        Assert.Equal(p1, p2);
        Assert.Equal(44, p1.Length); // base64 of 32 bytes -> 44 chars
        Assert.Equal(32, Convert.FromBase64String(p1).Length);

        // 参考:PBKDF2-SHA256(pw, salt=seed, iter=100000, 32B) -> base64
        var expected = Convert.ToBase64String(
            Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes("p4ssw0rd"),
                seed,
                100_000,
                HashAlgorithmName.SHA256,
                32));
        Assert.Equal(expected, p1);
    }

    [Fact]
    public void ComputePasswordProof_DiffersForDifferentPasswords()
    {
        var svc = NewService();
        var seed = svc.GenerateSeed();

        Assert.NotEqual(
            svc.ComputePasswordProof("password-a", seed),
            svc.ComputePasswordProof("password-b", seed));
    }

    [Fact]
    public void BuildShareUrl_TryParseShareUrl_RoundTripsAccessIdAndSeed()
    {
        var svc = NewService();
        var seed = svc.GenerateSeed();
        const string accessId = "abc-123_XY";

        var url = svc.BuildShareUrl("https://vault.example/", accessId, seed);

        // 不含末尾重复斜杠,且含锚点路由
        Assert.Equal("https://vault.example/#/send/abc-123_XY/" + Base64UrlNoPad(seed), url);

        var ok = svc.TryParseShareUrl(url, out var parsedAccessId, out var parsedSeed);
        Assert.True(ok);
        Assert.Equal(accessId, parsedAccessId);
        Assert.Equal(seed, parsedSeed);
    }

    [Fact]
    public void TryParseShareUrl_InvalidUrl_ReturnsFalse()
    {
        var svc = NewService();

        var ok = svc.TryParseShareUrl("https://vault.example/#/login", out var accessId, out var seed);

        Assert.False(ok);
        Assert.Equal("", accessId);
        Assert.Empty(seed);
    }

    private static string Base64UrlNoPad(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
