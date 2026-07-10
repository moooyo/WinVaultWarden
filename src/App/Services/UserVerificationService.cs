using System.Security.Cryptography;
using Core.Abstractions;
using Core.Services;

namespace App.Services;

// 真实用户验证:required passkey / 详情解锁等场景据此产出 UV=true。
// 优先尝试 Windows Hello 手势(对随机 challenge 真实签名——非空签名即代表用户完成了手势验证),
// 不可用/失败/取消再回退主密码。二者皆失败返回 false。绝不凭"Hello 可用"就判定已验证。
public interface IUserVerificationService
{
    Task<bool> VerifyAsync(
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default);
}

public sealed class UserVerificationService(
    IWindowsHelloAuthenticator hello,
    IAccountService account) : IUserVerificationService
{
    public async Task<bool> VerifyAsync(
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        if (await hello.IsAvailableAsync())
        {
            var challenge = RandomNumberGenerator.GetBytes(32);
            try
            {
                var signature = await hello.GetProtectorAsync(challenge, ct);
                if (signature is not null)
                {
                    CryptographicOperations.ZeroMemory(signature);
                    return true;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(challenge);
            }
        }

        var password = await prompt(ct);
        return password is not null
            && await account.VerifyMasterPasswordAsync(password, ct);
    }
}
