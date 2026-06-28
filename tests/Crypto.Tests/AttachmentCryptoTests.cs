using System.Security.Cryptography;
using System.Text;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class AttachmentCryptoTests
{
    private static SymmetricCryptoKey NewItemKey() => new(RandomNumberGenerator.GetBytes(64));
    private static AttachmentCryptoService NewService() => new(new CryptoService());

    [Fact]
    public void GenerateAttachmentKey_Returns64ByteRandomKey()
    {
        var svc = NewService();

        var a = svc.GenerateAttachmentKey();
        var b = svc.GenerateAttachmentKey();

        Assert.Equal(64, a.FullKey.Length);
        Assert.Equal(32, a.EncKey.Length);
        Assert.NotNull(a.MacKey);
        Assert.Equal(32, a.MacKey!.Length);
        // 随机性:两次生成不同
        Assert.NotEqual(a.FullKey, b.FullKey);
    }

    [Fact]
    public void WrapKey_UnwrapKey_RoundTripsUnderItemKey()
    {
        var svc = NewService();
        var itemKey = NewItemKey();
        var attKey = svc.GenerateAttachmentKey();

        var wrapped = svc.WrapKey(attKey, itemKey);
        var unwrapped = svc.UnwrapKey(wrapped, itemKey);

        // itemKey 带 MacKey,包裹密文为 type 2(AesCbc256_HmacSha256)
        Assert.StartsWith("2.", wrapped);
        Assert.Equal(attKey.FullKey, unwrapped.FullKey);
    }

    [Fact]
    public void EncryptFileName_DecryptFileName_RoundTrips()
    {
        var svc = NewService();
        var attKey = svc.GenerateAttachmentKey();

        var enc = svc.EncryptFileName("我的报告 report.pdf", attKey);
        var dec = svc.DecryptFileName(enc, attKey);

        Assert.StartsWith("2.", enc);
        Assert.Equal("我的报告 report.pdf", dec);
    }

    [Fact]
    public void DecryptFileName_NullOrEmpty_ReturnsNull()
    {
        var svc = NewService();
        var attKey = svc.GenerateAttachmentKey();

        Assert.Null(svc.DecryptFileName(null, attKey));
        Assert.Null(svc.DecryptFileName("", attKey));
    }

    [Fact]
    public void EncryptFile_DecryptFile_RoundTrips_AndHeaderByteIs2()
    {
        var svc = NewService();
        var attKey = svc.GenerateAttachmentKey();
        var plaintext = Encoding.UTF8.GetBytes("attachment file body bytes — éèê");

        var buffer = svc.EncryptFile(plaintext, attKey);

        // EncArrayBuffer 头字节 = 2(AesCbc256_HmacSha256)
        Assert.Equal(2, buffer[0]);
        // 布局:1 + iv(16) + mac(32) + ct(...)
        var ctLen = buffer.Length - 1 - 16 - 32;
        Assert.True(ctLen > 0);

        var dec = svc.DecryptFile(buffer, attKey);
        Assert.Equal(plaintext, dec);
    }

    [Fact]
    public void EncryptFile_EmptyPlaintext_RoundTrips()
    {
        var svc = NewService();
        var attKey = svc.GenerateAttachmentKey();

        var buffer = svc.EncryptFile(Array.Empty<byte>(), attKey);
        var dec = svc.DecryptFile(buffer, attKey);

        Assert.Equal(2, buffer[0]);
        Assert.Empty(dec);
    }

    [Fact]
    public void UnwrapKey_FromWrappedString_ProducesUsableKey()
    {
        var svc = NewService();
        var itemKey = NewItemKey();
        var attKey = svc.GenerateAttachmentKey();
        var plaintext = Encoding.UTF8.GetBytes("round-trip through wrapped key");

        // 用原 attKey 加密文件,再用 unwrap 出来的 key 解密,结果须一致
        var buffer = svc.EncryptFile(plaintext, attKey);
        var wrapped = svc.WrapKey(attKey, itemKey);
        var recovered = svc.UnwrapKey(wrapped, itemKey);

        var dec = svc.DecryptFile(buffer, recovered);
        Assert.Equal(plaintext, dec);
    }
}
