using Crypto;
using Xunit;

namespace Crypto.Tests;

public class TotpGeneratorTests
{
    // RFC 6238 Appendix B — SHA1 test vectors
    // Secret: ASCII "12345678901234567890"
    // Base32 encoding: GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ
    private const string Secret20 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Fact]
    public void Generate_matches_rfc6238_vector_t59()
    {
        // T = 59 → counter = 59/30 = 1 → expected TOTP = 287082
        Assert.Equal("287082", TotpGenerator.Generate(Secret20, 59));
    }

    [Fact]
    public void Generate_matches_rfc6238_vector_t1111111109()
    {
        // T = 1111111109 → counter = 1111111109/30 = 37037036 → expected = 081804
        Assert.Equal("081804", TotpGenerator.Generate(Secret20, 1111111109));
    }

    [Fact]
    public void Generate_throws_FormatException_on_invalid_base32_char()
    {
        // '!' is not in the Base32 alphabet; must throw FormatException, not silently corrupt
        var ex = Assert.Throws<FormatException>(() => TotpGenerator.Generate("AAAA!AAA", 0));
        Assert.Contains("!", ex.Message);
    }

    // ── S9: TryGenerate 从裸密钥 / otpauth URI 生成码;无法解析返回 false ──────

    [Fact]
    public void TryGenerate_BareBase32Secret_MatchesGenerate()
    {
        Assert.True(TotpGenerator.TryGenerate(Secret20, 59, out var code));
        Assert.Equal("287082", code);
    }

    [Fact]
    public void TryGenerate_OtpauthUri_ExtractsSecretAndGenerates()
    {
        var uri = $"otpauth://totp/Acme:alice@example.com?secret={Secret20}&issuer=Acme&digits=6&period=30";
        Assert.True(TotpGenerator.TryGenerate(uri, 59, out var code));
        Assert.Equal("287082", code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AAAA!AAA")]                       // 坏 Base32
    [InlineData("otpauth://totp/x?issuer=Acme")]   // otpauth 缺 secret
    [InlineData("otpauth://totp/x?secret=AAAA!A")] // otpauth 坏 secret
    public void TryGenerate_Unparseable_ReturnsFalse(string? totp)
    {
        Assert.False(TotpGenerator.TryGenerate(totp, 59, out var code));
        Assert.Equal(string.Empty, code);
    }
}
