using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Konscious.Security.Cryptography;

namespace Crypto;

// Bitwarden 加密链实现。严格遵循安全白皮书 + 独立实现核实。
public class CryptoService
{
    public byte[] DeriveMasterKey(string password, string email, KdfType kdfType, int iterations, int? memoryMiB, int? parallelism)
    {
        ValidateKdfParameters(kdfType, iterations, memoryMiB, parallelism);

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

            // Argon2id:salt = SHA-256(规范化邮箱);memory 单位 KiB = MiB*1024。
            KdfType.Argon2id => DeriveArgon2idMasterKey(pw, normalizedEmail, iterations, memoryMiB, parallelism),

            _ => throw new ArgumentOutOfRangeException(nameof(kdfType)),
        };
    }

    // KDF 参数边界校验。合法参数必须原样派生，绝不 clamp，否则会得到错误密钥。
    private const int MinPbkdf2Iterations = 100_000;   // Bitwarden 默认 600000
    private const int MinArgon2Iterations = 1;
    private const int MinArgon2MemoryMiB = 15;
    private const int MaxArgon2MemoryMiB = 1024;        // DoS 上限
    private const int MinArgon2Parallelism = 1;

    private static void ValidateKdfParameters(KdfType kdfType, int iterations, int? memoryMiB, int? parallelism)
    {
        switch (kdfType)
        {
            case KdfType.Pbkdf2:
                if (iterations < MinPbkdf2Iterations)
                    throw new CryptographicException(
                        $"服务器返回的 PBKDF2 迭代次数({iterations})低于安全下限 {MinPbkdf2Iterations},拒绝派生密钥(服务器 KDF 参数不可信)。");
                break;

            case KdfType.Argon2id:
                if (iterations < MinArgon2Iterations)
                    throw new CryptographicException(
                        $"服务器返回的 Argon2id 迭代次数({iterations})低于协议下限 {MinArgon2Iterations},拒绝派生密钥。");
                // memory/parallelism 为 null(缺参数)交由 DeriveArgon2idMasterKey 抛 ArgumentException;
                // 此处仅对已给出的值做边界校验,以保持既有 null 行为不变。
                if (memoryMiB is int mem)
                {
                    if (mem < MinArgon2MemoryMiB)
                        throw new CryptographicException(
                            $"服务器返回的 Argon2id 内存({mem} MiB)低于安全下限 {MinArgon2MemoryMiB} MiB,拒绝派生密钥。");
                    if (mem > MaxArgon2MemoryMiB)
                        throw new CryptographicException(
                            $"服务器返回的 Argon2id 内存({mem} MiB)超过上限 {MaxArgon2MemoryMiB} MiB,拒绝派生密钥(防 DoS)。");
                }
                if (parallelism is int par && par < MinArgon2Parallelism)
                    throw new CryptographicException(
                        $"服务器返回的 Argon2id 并行度({par})低于安全下限 {MinArgon2Parallelism},拒绝派生密钥。");
                break;

            // 未知 kdfType 不在此处拦截,交由 DeriveMasterKey 主 switch 的 ArgumentOutOfRangeException 处理。
        }
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
    public virtual SymmetricCryptoKey StretchMasterKey(byte[] masterKey)
    {
        var enc = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "enc"u8.ToArray());
        var mac = HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "mac"u8.ToArray());
        return new SymmetricCryptoKey(enc, mac);
    }

    // 由外部机密(Windows Hello 对固定挑战的签名)派生 64 字节对称密钥(EncKey32‖MacKey32)。
    // 与 StretchMasterKey 同构:两次独立 HKDF-Expand(SHA256),info 分别为 "enc"/"mac"。
    // 签名对同一挑战确定 → 派生密钥稳定,可跨会话解开 Hello 包住的 UserKey。
    public SymmetricCryptoKey DeriveKeyFromSecret(byte[] secret)
    {
        var enc = HKDF.Expand(HashAlgorithmName.SHA256, secret, 32, "enc"u8.ToArray());
        var mac = HKDF.Expand(HashAlgorithmName.SHA256, secret, 32, "mac"u8.ToArray());
        return new SymmetricCryptoKey(enc, mac);
    }

    // 用新主密钥拉伸后重新包裹现有 UserKey(type-2),DecryptUserKey 的逆;UserKey 本体不变。
    // stretched key 仅在此方法内存活，用完立即清零，防止在 GC 之前泄漏到堆上。
    public EncString ProtectUserKey(byte[] masterKey, SymmetricCryptoKey userKey)
    {
        var stretched = StretchMasterKey(masterKey);
        try { return Encrypt(userKey.FullKey, stretched); }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(stretched.EncKey);
            if (stretched.MacKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(stretched.MacKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(stretched.FullKey);
        }
    }

    // 用拉伸子钥解开 protected user key,得到真正的 UserKey。
    public virtual SymmetricCryptoKey DecryptUserKey(SymmetricCryptoKey stretchedKey, EncString protectedUserKey)
    {
        var plain = Decrypt(protectedUserKey, stretchedKey);
        return new SymmetricCryptoKey(plain);
    }

    // AES-256-CBC 解密,先验 HMAC(覆盖 IV‖ct)。type 0 无 MAC 跳过验证。
    public byte[] Decrypt(EncString data, SymmetricCryptoKey key)
    {
        // 拒绝 MAC 剥离降级:MAC-capable 密钥(用户/条目/附件/Send 密钥,全为 64 字节)
        // 绝不解密无 MAC 的 type-0 密文——否则恶意/MITM 服务端可把认证的 type-2 字段改写成
        // 无认证的 type-0,击穿 enc-then-MAC(完整性伪造 + CBC padding oracle)。
        // 本代码库所有密钥都是 MAC-capable,此检查不拒绝任何合法数据。
        if (key.MacKey is not null && data.Mac is null)
            throw new CryptographicException("MAC 密钥不得解密无 MAC 密文(拒绝降级)");

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

    // Decode an encrypted string as UTF-8 text for vault read projections.
    public string? DecryptToString(string? encStringText, SymmetricCryptoKey key)
    {
        if (string.IsNullOrWhiteSpace(encStringText))
            return null;

        var bytes = Decrypt(EncString.Parse(encStringText), key);
        return Encoding.UTF8.GetString(bytes);
    }

    // Decrypt an item-level key encrypted by the user key.
    public SymmetricCryptoKey DecryptItemKey(string cipherKeyEnc, SymmetricCryptoKey userKey)
    {
        var bytes = Decrypt(EncString.Parse(cipherKeyEnc), userKey);
        return new SymmetricCryptoKey(bytes);
    }

    // RSA 加密(OAEP-SHA1, encType 4)。publicKeyDer 为 SubjectPublicKeyInfo DER。DecryptRsa 的逆。
    public EncString EncryptRsa(byte[] plaintext, byte[] publicKeyDer)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyDer, out _);
        var ct = rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA1);
        return new EncString(EncryptionType.Rsa2048_OaepSha1_B64, Array.Empty<byte>(), ct, null);
    }

    // RSA 解密(OAEP)。type 3 用 SHA256,type 4 用 SHA1。privateKeyDer 为 PKCS8 DER。
    public byte[] DecryptRsa(EncString data, byte[] privateKeyDer)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateKeyDer, out _);

        var padding = data.Type switch
        {
            EncryptionType.Rsa2048_OaepSha256_B64 => RSAEncryptionPadding.OaepSHA256,
            EncryptionType.Rsa2048_OaepSha1_B64 => RSAEncryptionPadding.OaepSHA1,
            _ => throw new CryptographicException($"非 RSA encType: {(int)data.Type}"),
        };
        return rsa.Decrypt(data.Ct, padding);
    }

    private static byte[] DeriveArgon2idMasterKey(byte[] password, string normalizedEmail, int iterations, int? memoryMiB, int? parallelism)
    {
        if (memoryMiB is null)
            throw new ArgumentException("Argon2id 需要 memory 参数(prelogin 应返回)。", nameof(memoryMiB));
        if (parallelism is null)
            throw new ArgumentException("Argon2id 需要 parallelism 参数(prelogin 应返回)。", nameof(parallelism));

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail));
        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            Iterations = iterations,
            MemorySize = checked(memoryMiB.Value * 1024),
            DegreeOfParallelism = parallelism.Value,
        };
        return argon2.GetBytes(32);
    }

    private static byte[] ComputeMac(byte[] macKey, byte[] iv, byte[] ct)
    {
        using var hmac = new HMACSHA256(macKey);
        hmac.TransformBlock(iv, 0, iv.Length, null, 0);
        hmac.TransformFinalBlock(ct, 0, ct.Length);
        return hmac.Hash!;
    }
}
