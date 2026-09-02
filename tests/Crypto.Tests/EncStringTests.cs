using Core.Enums;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class EncStringTests
{
    [Fact]
    public void Parse_Type2_SplitsIvCtMac()
    {
        // 构造 type 2:iv(16B)|ct(16B)|mac(32B),均 base64
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var mac = Convert.ToBase64String(new byte[32]);
        var s = $"2.{iv}|{ct}|{mac}";

        var enc = EncString.Parse(s);

        Assert.Equal(EncryptionType.AesCbc256_HmacSha256_B64, enc.Type);
        Assert.Equal(16, enc.Iv.Length);
        Assert.Equal(16, enc.Ct.Length);
        Assert.NotNull(enc.Mac);
        Assert.Equal(32, enc.Mac!.Length);
    }

    [Fact]
    public void Parse_Type0_HasNoMac()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var s = $"0.{iv}|{ct}";

        var enc = EncString.Parse(s);

        Assert.Equal(EncryptionType.AesCbc256_B64, enc.Type);
        Assert.Null(enc.Mac);
    }

    [Fact]
    public void ToString_RoundTripsType2()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var mac = Convert.ToBase64String(new byte[32]);
        var s = $"2.{iv}|{ct}|{mac}";

        Assert.Equal(s, EncString.Parse(s).ToString());
    }

    // 健壮性:非数字类型前缀 → 干净 FormatException(而非其他异常)。
    [Fact]
    public void Parse_NonNumericPrefix_ThrowsFormatException()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        Assert.Throws<FormatException>(() => EncString.Parse($"x.{iv}|{ct}"));
    }

    // 健壮性:超长(溢出 int)类型前缀 → FormatException,而非 int.Parse 的 OverflowException。
    [Fact]
    public void Parse_OverflowPrefix_ThrowsFormatException_NotOverflow()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var ex = Record.Exception(() => EncString.Parse($"99999999999999999999.{iv}|{ct}"));
        Assert.IsType<FormatException>(ex);
        Assert.IsNotType<OverflowException>(ex);
    }

    // 健壮性:type 2 只有 2 段(缺 mac) → FormatException,而非访问 parts[2] 的 IndexOutOfRangeException。
    [Fact]
    public void Parse_Type2_Truncated_ThrowsFormatException_NotIndexOutOfRange()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ct = Convert.ToBase64String(new byte[16]);
        var ex = Record.Exception(() => EncString.Parse($"2.{iv}|{ct}"));
        Assert.IsType<FormatException>(ex);
        Assert.IsNotType<IndexOutOfRangeException>(ex);
    }

    // 健壮性:type 0 只有 1 段(缺 ct) → FormatException,而非访问 parts[1] 的 IndexOutOfRangeException。
    [Fact]
    public void Parse_Type0_Truncated_ThrowsFormatException_NotIndexOutOfRange()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var ex = Record.Exception(() => EncString.Parse($"0.{iv}"));
        Assert.IsType<FormatException>(ex);
        Assert.IsNotType<IndexOutOfRangeException>(ex);
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void Parse_LegacyRsaHmac_RoundTripsExactDataAndMac(EncryptionType type)
    {
        var data = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
        var mac = Enumerable.Range(1, EncString.LegacyRsaMacLength)
            .Select(index => (byte)index)
            .ToArray();
        var serialized = $"{(int)type}.{Convert.ToBase64String(data)}|{Convert.ToBase64String(mac)}";

        var parsed = EncString.Parse(serialized);

        Assert.Equal(type, parsed.Type);
        Assert.Empty(parsed.Iv);
        Assert.Equal(data, parsed.Ct);
        Assert.Equal(mac, parsed.Mac);
        Assert.Equal(serialized, parsed.ToString());
    }

    [Fact]
    public void Parse_AllSupportedTypes_RejectTrailingSegments()
    {
        var iv = Convert.ToBase64String(new byte[16]);
        var data = Convert.ToBase64String(new byte[16]);
        var mac = Convert.ToBase64String(new byte[EncString.LegacyRsaMacLength]);
        string[] malformed =
        [
            $"0.{iv}|{data}|{mac}",
            $"1.{iv}|{data}|{mac}|{mac}",
            $"2.{iv}|{data}|{mac}|{mac}",
            $"3.{data}|{mac}",
            $"4.{data}|{mac}",
            $"5.{data}|{mac}|{mac}",
            $"6.{data}|{mac}|{mac}",
        ];

        Assert.All(malformed, value =>
            Assert.Throws<FormatException>(() => EncString.Parse(value)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Parse_LegacyRsaHmac_RejectsNonSha256MacLength(int macLength)
    {
        var data = Convert.ToBase64String(new byte[256]);
        var mac = Convert.ToBase64String(new byte[macLength]);

        Assert.Throws<FormatException>(() => EncString.Parse($"5.{data}|{mac}"));
        Assert.Throws<FormatException>(() => EncString.Parse($"6.{data}|{mac}"));
    }

    [Theory]
    [InlineData(EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64)]
    [InlineData(EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64)]
    public void ToString_LegacyRsaHmac_RejectsMalformedProgrammaticValue(
        EncryptionType type)
    {
        var malformed = new EncString(type, [], new byte[256], new byte[31]);

        Assert.Throws<InvalidOperationException>(() => malformed.ToString());
    }
}
