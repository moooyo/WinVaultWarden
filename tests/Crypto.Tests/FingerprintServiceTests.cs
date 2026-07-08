using Crypto;
using Xunit;

namespace Crypto.Tests;

public class FingerprintServiceTests
{
    // 金标准向量: 来自 bitwarden/clients libs/key-management/src/key.service.spec.ts
    // 的 getFingerprint 测试 (hash/hkdfExpand 被 mock, 故该向量精确锁定 hashPhrase 这一步:
    // base-256 大端整数 → 逐次 mod/div 7776 → EFF long wordlist 取词)。
    [Fact]
    public void HashPhrase_MatchesBitwardenGoldenVector()
    {
        var userFingerprint = System.Convert.FromBase64String(
            "V5AQSk83YXd6kZqCncC6d9J72R7UZ60Xl1eIoDoWgTc=");

        var words = FingerprintService.HashPhrase(userFingerprint);

        Assert.Equal(
            new[] { "predefine", "hunting", "pastime", "enrich", "unhearing" },
            words);
    }
}
