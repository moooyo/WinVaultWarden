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
}
