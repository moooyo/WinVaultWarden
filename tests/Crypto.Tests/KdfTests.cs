using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Crypto;
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
    public void DeriveMasterKey_Argon2id_Throws()
    {
        var svc = new CryptoService();
        Assert.Throws<NotImplementedException>(() =>
            svc.DeriveMasterKey(Password, Email, KdfType.Argon2id, 3, 64, 4));
    }
}
