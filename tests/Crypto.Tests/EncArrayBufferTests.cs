using System.Security.Cryptography;
using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class EncArrayBufferTests
{
    private static EncString NewEnc()
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        var ct = RandomNumberGenerator.GetBytes(48);
        var mac = RandomNumberGenerator.GetBytes(32);
        return new EncString(EncryptionType.AesCbc256_HmacSha256_B64, iv, ct, mac);
    }

    [Fact]
    public void Pack_ProducesType2HeaderThenIvMacCt()
    {
        var enc = NewEnc();

        var buffer = EncArrayBuffer.Pack(enc);

        // 布局: [ (byte)2, iv(16), mac(32), ct(...) ]
        Assert.Equal(2, buffer[0]);
        Assert.Equal(1 + 16 + 32 + enc.Ct.Length, buffer.Length);
        Assert.Equal(enc.Iv, buffer[1..17]);
        Assert.Equal(enc.Mac, buffer[17..49]);
        Assert.Equal(enc.Ct, buffer[49..]);
    }

    [Fact]
    public void Pack_Unpack_RoundTrips()
    {
        var enc = NewEnc();

        var buffer = EncArrayBuffer.Pack(enc);
        var back = EncArrayBuffer.Unpack(buffer);

        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, back.Type);
        Assert.Equal(enc.Iv, back.Iv);
        Assert.NotNull(back.Mac);
        Assert.Equal(enc.Mac, back.Mac!);
        Assert.Equal(enc.Ct, back.Ct);
    }

    [Fact]
    public void Pack_WithoutMac_Throws()
    {
        var enc = new EncString(
            EncryptionType.AesCbc256_B64,
            RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(48),
            mac: null);

        Assert.Throws<CryptographicException>(() => EncArrayBuffer.Pack(enc));
    }

    [Fact]
    public void Unpack_TooShort_Throws()
    {
        var buffer = new byte[48];
        buffer[0] = 2;

        Assert.Throws<CryptographicException>(() => EncArrayBuffer.Unpack(buffer));
    }

    [Fact]
    public void Unpack_WrongHeaderByte_Throws()
    {
        var buffer = new byte[49];
        buffer[0] = 1; // 非 EncArrayBuffer 头

        Assert.Throws<CryptographicException>(() => EncArrayBuffer.Unpack(buffer));
    }

    [Fact]
    public void Unpack_AtMinimumLength_YieldsEmptyCt()
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        var mac = RandomNumberGenerator.GetBytes(32);
        var buffer = new byte[49];
        buffer[0] = 2;
        System.Buffer.BlockCopy(iv, 0, buffer, 1, 16);
        System.Buffer.BlockCopy(mac, 0, buffer, 17, 32);

        var enc = EncArrayBuffer.Unpack(buffer);

        Assert.Equal(iv, enc.Iv);
        Assert.Equal(mac, enc.Mac!);
        Assert.Empty(enc.Ct);
    }
}
