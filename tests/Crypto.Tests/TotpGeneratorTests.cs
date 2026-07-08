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

    // ── C1: algorithm-aware TOTP (RFC 6238 Appendix B, digits=8) ─────────────
    // RFC 6238 secrets (ASCII repeated): SHA256 uses a 32-byte key, SHA512 a 64-byte key.
    // Base32 (unpadded, as it appears in an otpauth secret= value):
    private const string Sha256SecretB32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA";
    private const string Sha512SecretB32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA";

    [Theory]
    [InlineData(59, "46119246")]
    [InlineData(1111111109, "68084774")]
    [InlineData(1234567890, "91819424")]
    [InlineData(2000000000, "90698825")]
    public void Generate_matches_rfc6238_sha256_vectors(long t, string expected)
    {
        Assert.Equal(expected, TotpGenerator.Generate(Sha256SecretB32, t, digits: 8, period: 30, algorithm: TotpAlgorithm.Sha256));
    }

    [Theory]
    [InlineData(59, "90693936")]
    [InlineData(1111111109, "25091201")]
    [InlineData(1234567890, "93441116")]
    [InlineData(2000000000, "38618901")]
    public void Generate_matches_rfc6238_sha512_vectors(long t, string expected)
    {
        Assert.Equal(expected, TotpGenerator.Generate(Sha512SecretB32, t, digits: 8, period: 30, algorithm: TotpAlgorithm.Sha512));
    }

    [Fact]
    public void TryGenerate_OtpauthSha256_UsesSha256()
    {
        var uri = $"otpauth://totp/Acme:alice@example.com?secret={Sha256SecretB32}&algorithm=SHA256&digits=8&period=30";
        Assert.True(TotpGenerator.TryGenerate(uri, 59, out var code));
        Assert.Equal("46119246", code);
    }

    [Fact]
    public void TryGenerate_OtpauthSha512_UsesSha512()
    {
        var uri = $"otpauth://totp/Acme:alice@example.com?secret={Sha512SecretB32}&algorithm=SHA512&digits=8&period=30";
        Assert.True(TotpGenerator.TryGenerate(uri, 59, out var code));
        Assert.Equal("90693936", code);
    }

    [Fact]
    public void TryGenerate_OtpauthMissingOrUnknownAlgorithm_DefaultsToSha1()
    {
        // No algorithm= param → SHA1 (existing Secret20 / RFC SHA1 T=59 → 6-digit 287082).
        var uri = $"otpauth://totp/x?secret={Secret20}&digits=6&period=30";
        Assert.True(TotpGenerator.TryGenerate(uri, 59, out var code));
        Assert.Equal("287082", code);
    }

    // ── C1: Steam Guard (HMAC-SHA1, 5-char base-26 alphabet) ─────────────────
    // Reference steamguard over the 20-byte RFC secret (Secret20) at T=59 → "PV9M4".
    [Fact]
    public void GenerateSteam_matches_reference_vector()
    {
        Assert.Equal("PV9M4", TotpGenerator.GenerateSteam(Secret20, 59));
    }

    [Fact]
    public void TryGenerate_SteamUri_ProducesFiveCharCode()
    {
        Assert.True(TotpGenerator.TryGenerate($"steam://{Secret20}", 59, out var code));
        Assert.Equal("PV9M4", code);
        Assert.Equal(5, code.Length);
    }

    [Theory]
    [InlineData("steam://")]            // 空 steam 密钥
    [InlineData("steam://AAAA!A")]      // steam 坏 Base32
    public void TryGenerate_BadSteam_ReturnsFalse(string totp)
    {
        Assert.False(TotpGenerator.TryGenerate(totp, 59, out var code));
        Assert.Equal(string.Empty, code);
    }
}
