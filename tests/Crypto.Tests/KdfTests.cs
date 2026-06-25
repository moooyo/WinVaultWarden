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
    public void DeriveMasterKey_Argon2id_MatchesParameterMapping()
    {
        var svc = new CryptoService();
        const int iterations = 3, memoryMiB = 64, parallelism = 4;

        var actual = svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, iterations, memoryMiB, parallelism);

        // 参考实现:salt = SHA256(规范化邮箱),memory 单位 KiB = MiB*1024。
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(Email));
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(Password))
        {
            Salt = salt,
            Iterations = iterations,
            MemorySize = memoryMiB * 1024,
            DegreeOfParallelism = parallelism,
        };
        var expected = argon2.GetBytes(32);

        Assert.Equal(expected, actual);
        Assert.Equal(32, actual.Length);
    }

    [Fact]
    public void DeriveMasterKey_Argon2id_RejectsWrongMemoryUnit()
    {
        var svc = new CryptoService();
        var actual = svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, 3, 64, 4);

        // 若实现误把 MiB 当 KiB(不 *1024),结果会等于下面这个错误参考值;断言两者不同。
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(Email));
        using var wrong = new Argon2id(Encoding.UTF8.GetBytes(Password))
        {
            Salt = salt,
            Iterations = 3,
            MemorySize = 64, // 错误:把 MiB 直接当 KiB
            DegreeOfParallelism = 4,
        };
        Assert.NotEqual(wrong.GetBytes(32), actual);
    }

    [Fact]
    public void DeriveMasterKey_Argon2id_NullMemoryOrParallelism_Throws()
    {
        var svc = new CryptoService();
        Assert.Throws<ArgumentException>(() =>
            svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, 3, null, 4));
        Assert.Throws<ArgumentException>(() =>
            svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, 3, 64, null));
    }
}
