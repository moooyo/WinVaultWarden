using System.Security.Cryptography;
using System.Text;
using Core.Abstractions;
using Core.Enums;

namespace Crypto;

// Bitwarden 加密链实现。严格遵循安全白皮书 + 独立实现核实。
public sealed class CryptoService : ICryptoService
{
    public byte[] DeriveMasterKey(string password, string email, KdfType kdfType, int iterations, int? memoryMiB, int? parallelism)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var normalizedEmail = email.Trim().ToLowerInvariant();

        return kdfType switch
        {
            // PBKDF2:salt = 原始邮箱字节
            KdfType.Pbkdf2 => Rfc2898DeriveBytes.Pbkdf2(
                pw,
                Encoding.UTF8.GetBytes(normalizedEmail),
                iterations,
                HashAlgorithmName.SHA256,
                32),

            // Argon2id:salt = SHA-256(邮箱);需第三方包,本次不实现
            KdfType.Argon2id => throw new NotImplementedException(
                "Argon2id 派生待实现:salt = SHA-256(邮箱),参数 iter/mem/parallel 来自 prelogin。引入 Konscious.Security.Cryptography.Argon2 后补全。"),

            _ => throw new ArgumentOutOfRangeException(nameof(kdfType)),
        };
    }

    public string ComputeMasterPasswordHash(byte[] masterKey, string password)
    {
        // PBKDF2-SHA256(pw = MasterKey, salt = 主密码, iter = 1) → base64
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            masterKey,
            Encoding.UTF8.GetBytes(password),
            1,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToBase64String(hash);
    }
}
