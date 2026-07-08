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

    // 端到端向量(由三个已核实原语复算,可独立复现,见任务说明):
    //   publicKey = 256 个 0 字节; material = "test@example.com"
    //   prk = SHA-256(publicKey); okm = HKDF-Expand(prk, utf8(material), 32, SHA256)
    //   hashPhrase(okm) → "visitor-precision-relight-irritate-request"
    [Fact]
    public void GetFingerprintPhrase_EndToEnd_MatchesReproducibleVector()
    {
        var svc = new FingerprintService();
        var publicKey = new byte[256];

        var phrase = svc.GetFingerprintPhrase(publicKey, "test@example.com");

        Assert.Equal("visitor-precision-relight-irritate-request", phrase);
    }

    // bitwarden getFingerprintPhrase 先做 email.toLowerCase();大小写不得影响结果。
    [Fact]
    public void GetFingerprintPhrase_LowercasesEmail()
    {
        var svc = new FingerprintService();
        var publicKey = new byte[256];

        Assert.Equal(
            svc.GetFingerprintPhrase(publicKey, "test@example.com"),
            svc.GetFingerprintPhrase(publicKey, "TEST@Example.COM"));
    }
}
