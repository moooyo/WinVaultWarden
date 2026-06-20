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
}
