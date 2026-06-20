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

    // 由 MasterKey 拉伸出 EncKey/MacKey(各一次独立 HKDF-Expand,SHA256)。
    public SymmetricCryptoKey StretchMasterKey(byte[] masterKey)
    {
        var enc = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "enc"u8.ToArray());
        var mac = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "mac"u8.ToArray());
        return new SymmetricCryptoKey(enc, mac);
    }

    // 用拉伸子钥解开 protected user key,得到真正的 UserKey。
    public SymmetricCryptoKey DecryptUserKey(SymmetricCryptoKey stretchedKey, EncString protectedUserKey)
    {
        var plain = Decrypt(protectedUserKey, stretchedKey);
        return new SymmetricCryptoKey(plain);
    }

    // AES-256-CBC 解密,先验 HMAC(覆盖 IV‖ct)。type 0 无 MAC 跳过验证。
    public byte[] Decrypt(EncString data, SymmetricCryptoKey key)
    {
        if (data.Mac is not null)
        {
            if (key.MacKey is null)
                throw new CryptographicException("密文带 MAC 但密钥无 MacKey");

            var computed = ComputeMac(key.MacKey, data.Iv, data.Ct);
            if (!CryptographicOperations.FixedTimeEquals(computed, data.Mac))
                throw new CryptographicException("MAC 校验失败");
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.EncKey;
        aes.IV = data.Iv;
        return aes.DecryptCbc(data.Ct, data.Iv);
    }

    // AES-256-CBC 加密,生成随机 IV,计算 MAC(覆盖 IV‖ct)。
    public EncString Encrypt(byte[] plaintext, SymmetricCryptoKey key)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.EncKey;
        aes.IV = iv;
        var ct = aes.EncryptCbc(plaintext, iv);

        byte[]? mac = key.MacKey is null ? null : ComputeMac(key.MacKey, iv, ct);
        var type = mac is null ? EncryptionType.AesCbc256_B64 : EncryptionType.AesCbc256_HmacSha256_B64;
        return new EncString(type, iv, ct, mac);
    }

    private static byte[] ComputeMac(byte[] macKey, byte[] iv, byte[] ct)
    {
        using var hmac = new HMACSHA256(macKey);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);
        hmac.TransformFinalBlock(ct, 0, ct.Length);
        return hmac.Hash!;
    }
}
