using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Core.Models;
using Konscious.Security.Cryptography;

namespace Crypto;

// Bitwarden 加密链实现。严格遵循安全白皮书 + 独立实现核实。
public class CryptoService
{
    public byte[] DeriveMasterKey(string password, string salt, KdfType kdfType, int iterations, int? memoryMiB, int? parallelism)
    {
        ArgumentNullException.ThrowIfNull(password);
        return DeriveMasterKey(
            password.AsSpan(),
            salt,
            kdfType,
            iterations,
            memoryMiB,
            parallelism);
    }

    public byte[] DeriveMasterKey(
        ReadOnlySpan<char> password,
        string salt,
        KdfType kdfType,
        int iterations,
        int? memoryMiB,
        int? parallelism)
    {
        MasterPasswordSaltPolicy.Validate(salt);
        ValidateKdfParameters(kdfType, iterations, memoryMiB, parallelism);

        var pw = GC.AllocateUninitializedArray<byte>(Encoding.UTF8.GetByteCount(password));
        try
        {
            Encoding.UTF8.GetBytes(password, pw);
            return kdfType switch
            {
                KdfType.Pbkdf2 => Rfc2898DeriveBytes.Pbkdf2(
                    pw,
                    Encoding.UTF8.GetBytes(salt),
                    iterations,
                    HashAlgorithmName.SHA256,
                    32),

                KdfType.Argon2id => DeriveArgon2idMasterKey(
                    pw,
                    salt,
                    iterations,
                    memoryMiB,
                    parallelism),

                _ => throw new ArgumentOutOfRangeException(nameof(kdfType)),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }

    // KDF 参数安全下限/上限校验。
    // 不可信/MITM 服务端可借 prelogin 下发极弱参数弱化整条派生链；所有派生入口(Login/Unlock/ChangePassword/ChangeKdf)
    // 共用这里的校验。绝不 clamp: clamp 会改写派生出的密钥并导致与服务端 wrappedKey 不匹配。
    public static void ValidateKdfParameters(KdfType kdfType, int iterations, int? memoryMiB, int? parallelism)
    {
        switch (kdfType)
        {
            case KdfType.Pbkdf2:
                if (iterations < KdfParameterPolicy.MinimumPbkdf2ReadIterations)
                    throw new CryptographicException(
                        $"服务器返回的 PBKDF2 迭代次数({iterations})低于安全下限 {KdfParameterPolicy.MinimumPbkdf2ReadIterations},拒绝派生密钥(服务器 KDF 参数不可信)。");
                if (iterations > KdfParameterPolicy.MaximumPbkdf2ReadIterations)
                    throw new CryptographicException(
                        $"服务器返回的 PBKDF2 迭代次数({iterations})超过防御上限 {KdfParameterPolicy.MaximumPbkdf2ReadIterations},拒绝派生密钥(防 DoS)。");
                break;

            case KdfType.Argon2id:
                if (iterations < KdfParameterPolicy.MinimumArgon2ReadIterations)
                    throw new CryptographicException(
                        $"服务器返回的 Argon2id 迭代次数({iterations})低于协议下限 {KdfParameterPolicy.MinimumArgon2ReadIterations},拒绝派生密钥。");
                if (iterations > KdfParameterPolicy.MaximumArgon2ReadIterations)
                    throw new CryptographicException(
                        $"服务器返回的 Argon2id 迭代次数({iterations})超过防御上限 {KdfParameterPolicy.MaximumArgon2ReadIterations},拒绝派生密钥(防 DoS)。");
                // memory/parallelism 为 null(缺参数)交由 DeriveArgon2idMasterKey 抛 ArgumentException;
                // 此处仅对已给出的值做边界校验,以保持既有 null 行为不变。
                if (memoryMiB is int mem)
                {
                    if (mem < KdfParameterPolicy.MinimumArgon2MemoryMiB)
                        throw new CryptographicException(
                            $"服务器返回的 Argon2id 内存({mem} MiB)低于安全下限 {KdfParameterPolicy.MinimumArgon2MemoryMiB} MiB,拒绝派生密钥。");
                    if (mem > KdfParameterPolicy.MaximumArgon2MemoryMiB)
                        throw new CryptographicException(
                            $"服务器返回的 Argon2id 内存({mem} MiB)超过上限 {KdfParameterPolicy.MaximumArgon2MemoryMiB} MiB,拒绝派生密钥(防 DoS)。");
                }
                if (parallelism is int par)
                {
                    if (par < KdfParameterPolicy.MinimumArgon2Parallelism)
                        throw new CryptographicException(
                            $"服务器返回的 Argon2id 并行度({par})低于安全下限 {KdfParameterPolicy.MinimumArgon2Parallelism},拒绝派生密钥。");
                    if (par > KdfParameterPolicy.MaximumArgon2Parallelism)
                        throw new CryptographicException(
                            $"服务器返回的 Argon2id 并行度({par})超过防御上限 {KdfParameterPolicy.MaximumArgon2Parallelism},拒绝派生密钥(防 DoS)。");
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kdfType));
        }
    }

    public string ComputeMasterPasswordHash(byte[] masterKey, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return ComputeMasterPasswordHash(masterKey, password.AsSpan());
    }

    public string ComputeMasterPasswordHash(
        byte[] masterKey,
        ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        var passwordBytes = GC.AllocateUninitializedArray<byte>(
            Encoding.UTF8.GetByteCount(password));
        byte[]? hash = null;
        try
        {
            Encoding.UTF8.GetBytes(password, passwordBytes);
            hash = Rfc2898DeriveBytes.Pbkdf2(
                masterKey,
                passwordBytes,
                1,
                HashAlgorithmName.SHA256,
                32);
            return Convert.ToBase64String(hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (hash is not null)
                CryptographicOperations.ZeroMemory(hash);
        }
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
        var plain = DecryptUserKeyMaterial(stretchedKey, protectedUserKey);
        try
        {
            return new SymmetricCryptoKey(plain);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plain);
            throw;
        }
    }

    public virtual byte[] DecryptUserKeyMaterial(
        SymmetricCryptoKey stretchedKey,
        EncString protectedUserKey) => Decrypt(protectedUserKey, stretchedKey);

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
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // Decrypt an item-level key encrypted by the user key.
    public SymmetricCryptoKey DecryptItemKey(string cipherKeyEnc, SymmetricCryptoKey userKey)
    {
        var bytes = Decrypt(EncString.Parse(cipherKeyEnc), userKey);
        return CreateSymmetricKeyFromOwnedBytes(bytes);
    }

    internal static SymmetricCryptoKey CreateSymmetricKeyFromOwnedBytes(byte[] bytes)
    {
        try
        {
            return new SymmetricCryptoKey(bytes);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    // New RSA envelopes always use OAEP-SHA1 and encryption type 4.
    public EncString EncryptRsa(byte[] plaintext, byte[] publicKeyDer)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyDer, out var bytesRead);
        if (bytesRead != publicKeyDer.Length || rsa.KeySize != 2048)
            throw new CryptographicException("RSA public key must be a complete 2048-bit SPKI value.");
        var ct = rsa.Encrypt(plaintext, RSAEncryptionPadding.OaepSHA1);
        return new EncString(EncryptionType.Rsa2048_OaepSha1_B64, Array.Empty<byte>(), ct, null);
    }

    // Historical type 5/6 MAC fields are structural legacy data. Bitwarden does not authenticate
    // them during decapsulation because the recipient has no sender-authentication key.
    public byte[] DecryptRsa(EncString data, byte[] privateKeyDer)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(privateKeyDer);
        ValidateRsaEnvelope(data);

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateKeyDer, out var bytesRead);
        if (bytesRead != privateKeyDer.Length || rsa.KeySize != 2048)
            throw new CryptographicException("RSA private key must be a complete 2048-bit PKCS#8 value.");

        var padding = data.Type switch
        {
            EncryptionType.Rsa2048_OaepSha256_B64
                or EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64 =>
                RSAEncryptionPadding.OaepSHA256,
            EncryptionType.Rsa2048_OaepSha1_B64
                or EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64 =>
                RSAEncryptionPadding.OaepSHA1,
            _ => throw new CryptographicException(
                $"Encryption type {(int)data.Type} is not an RSA envelope."),
        };
        return rsa.Decrypt(data.Ct, padding);
    }

    private static void ValidateRsaEnvelope(EncString data)
    {
        const int rsa2048CiphertextLength = 2048 / 8;
        var expectedMac = data.Type.HasLegacyRsaMac();
        if (!data.Type.IsRsaEnvelope()
            || data.Iv.Length != 0
            || data.Ct.Length != rsa2048CiphertextLength
            || (expectedMac && data.Mac is not { Length: EncString.LegacyRsaMacLength })
            || (!expectedMac && data.Mac is not null))
        {
            throw new CryptographicException("The RSA envelope structure is invalid.");
        }
    }

    private static byte[] DeriveArgon2idMasterKey(byte[] password, string salt, int iterations, int? memoryMiB, int? parallelism)
    {
        if (memoryMiB is null)
            throw new ArgumentException("Argon2id 需要 memory 参数(prelogin 应返回)。", nameof(memoryMiB));
        if (parallelism is null)
            throw new ArgumentException("Argon2id 需要 parallelism 参数(prelogin 应返回)。", nameof(parallelism));

        var saltHash = SHA256.HashData(Encoding.UTF8.GetBytes(salt));
        using var argon2 = new Argon2id(password)
        {
            Salt = saltHash,
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
