using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
using Konscious.Security.Cryptography;
using Xunit;

namespace Crypto.Tests;

public class KdfTests
{
    private const string Email = "nobody@example.com";
    private const string Password = "p4ssw0rd";
    private const int Iterations = 600_000;

    [Fact]
    public void DeriveMasterKey_Pbkdf2_MatchesReferenceImpl()
    {
        var svc = new CryptoService();

        var actual = svc.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);

        // 参考:PBKDF2-SHA256(pw=主密码, salt=原始邮箱, iter, 32B)
        var expected = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(Password),
            Encoding.UTF8.GetBytes(Email),
            Iterations,
            HashAlgorithmName.SHA256,
            32);

        Assert.Equal(expected, actual);
        Assert.Equal(32, actual.Length);
    }

    [Fact]
    public void ComputeMasterPasswordHash_MatchesReferenceImpl()
    {
        var svc = new CryptoService();
        var masterKey = svc.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, Iterations, null, null);

        var actual = svc.ComputeMasterPasswordHash(masterKey, Password);

        // 参考:PBKDF2-SHA256(pw=MasterKey, salt=主密码, iter=1, 32B) → base64
        var expectedBytes = Rfc2898DeriveBytes.Pbkdf2(
            masterKey,
            Encoding.UTF8.GetBytes(Password),
            1,
            HashAlgorithmName.SHA256,
            32);
        var expected = Convert.ToBase64String(expectedBytes);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DeriveMasterKey_Argon2id_IterationOne_MatchesVaultwardenCompatibleVector()
    {
        var svc = new CryptoService();

        var actual = svc.DeriveMasterKey(
            Password,
            Email,
            KdfType.Argon2id,
            iterations: 1,
            memoryMiB: 15,
            parallelism: 1);

        Assert.Equal(
            Convert.FromHexString("EF0BF4281B723A9AD1F5157F177AA53E107818063CC073D949CFF72C1800D96E"),
            actual);
    }

    // RFC 9106 Section 5.3 Argon2id known-answer test vector.
    // Validates Konscious against an independent published reference — independent of
    // our salt/param mapping so a library-internal regression would still be caught.
    [Fact]
    public void DeriveMasterKey_Argon2id_KonsciousMatchesRfc9106Vector()
    {
        // Inputs from RFC 9106 §5.3
        var password = Enumerable.Repeat((byte)0x01, 32).ToArray();
        var salt     = Enumerable.Repeat((byte)0x02, 16).ToArray();
        var secret   = Enumerable.Repeat((byte)0x03,  8).ToArray();
        var ad       = Enumerable.Repeat((byte)0x04, 12).ToArray();

        // Expected Tag from RFC 9106 §5.3 (32 bytes)
        var expectedTag = new byte[]
        {
            0x0d, 0x64, 0x0d, 0xf5, 0x8d, 0x78, 0x76, 0x6c,
            0x08, 0xc0, 0x37, 0xa3, 0x4a, 0x8b, 0x53, 0xc9,
            0xd0, 0x1e, 0xf0, 0x45, 0x2d, 0x75, 0xb6, 0x5e,
            0xb5, 0x25, 0x20, 0xe9, 0x6b, 0x01, 0xe6, 0x59,
        };

        using var argon2 = new Argon2id(password)
        {
            Salt                = salt,
            KnownSecret         = secret,
            AssociatedData      = ad,
            Iterations          = 3,
            MemorySize          = 32,    // KiB
            DegreeOfParallelism = 4,
        };
        var tag = argon2.GetBytes(32);

        Assert.Equal(expectedTag, tag);
    }

    // ── C2: KDF 参数下限校验(拒绝,不 clamp)。不可信服务端下发极弱参数弱化派生链。 ──

    [Fact]
    public void DeriveMasterKey_Pbkdf2_RejectsIterationsBelowFloor()
    {
        var svc = new CryptoService();
        // iterations=1:恶意/MITM 服务端弱化派生链 → 拒绝派生,抛 CryptographicException(不 clamp)。
        var ex = Assert.Throws<CryptographicException>(() =>
            svc.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, 1, null, null));
        Assert.Contains("100000", ex.Message);
    }

    [Fact]
    public void DeriveMasterKey_Pbkdf2_AcceptsFloorAndDefault()
    {
        var svc = new CryptoService();
        // 恰好下限(100000)与默认(600000)均放行,不抛异常。
        _ = svc.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, 100_000, null, null);
        _ = svc.DeriveMasterKey(Password, Email, KdfType.Pbkdf2, 600_000, null, null);
    }

    [Theory]
    [InlineData(0, 64, 4)]     // iterations < 1
    [InlineData(3, 14, 4)]     // memoryMiB < 15
    [InlineData(3, 1025, 4)]   // memoryMiB > 1024 (DoS 上限)
    [InlineData(3, 64, 0)]     // parallelism < 1
    public void DeriveMasterKey_Argon2id_RejectsOutOfBoundsParameters(int iterations, int memoryMiB, int parallelism)
    {
        var svc = new CryptoService();
        Assert.Throws<CryptographicException>(() =>
            svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, iterations, memoryMiB, parallelism));
    }

    [Fact]
    public void DeriveMasterKey_Argon2id_AcceptsInBoundsParameters()
    {
        var svc = new CryptoService();
        // 边界内(iterations>=1, 15<=memory<=1024, parallelism>=1)不抛异常。
        // 仅用廉价内存参数实际派生,避免 1024MiB 分配拖慢/撑爆 CI。
        _ = svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, 1, 15, 1);
        _ = svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, 3, 64, 4);
    }
}
