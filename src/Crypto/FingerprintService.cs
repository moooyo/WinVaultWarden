using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Crypto;

/// <summary>
/// Bitwarden 指纹短语算法(设备登录审批时展示、供用户与请求设备核对的 5 词短语)。
/// 必须与 bitwarden/clients 逐字一致,否则本端短语与请求设备显示的不一致,校验失去意义。
/// 参考(2026-07-08 核对):
///   libs/key-management/src/key.service.ts                 getFingerprint / hashPhrase
///   libs/common/.../web-crypto-function.service.ts         hkdfExpand (RFC 5869 Expand)
///   libs/auth/.../auth-request/auth-request.service.ts     getFingerprintPhrase(email.toLowerCase())
/// 算法:
///   prk = SHA-256(publicKey)                                    // publicKey = SPKI DER 原始字节
///   okm = HKDF-Expand(prk, info = UTF-8(material), L=32, SHA256)
///   hashPhrase(okm): 把 okm 视为 base-256 大端无符号整数,循环 5 次:
///     word[i] = EFFLongWordList[num % 7776]; num /= 7776
/// </summary>
public sealed class FingerprintService
{
    private const int WordCount = 7776; // EFF long wordlist 词数
    private const int PhraseWords = 5;  // ceil(64 / log2(7776)) = 5
    private static readonly string[] Words = LoadWordList();

    /// <summary>
    /// 由请求设备公钥(SPKI DER)+ 指纹材料派生 5 个词。material 按原样使用(不改大小写)。
    /// prk = SHA-256(publicKeyDer); okm = HKDF-Expand(prk, UTF-8(material), 32, SHA256)。
    /// </summary>
    public string[] GetFingerprint(byte[] publicKeyDer, string material)
    {
        ArgumentNullException.ThrowIfNull(publicKeyDer);
        ArgumentNullException.ThrowIfNull(material);

        var prk = SHA256.HashData(publicKeyDer);
        var okm = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Encoding.UTF8.GetBytes(material));
        try
        {
            return HashPhrase(okm);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
            CryptographicOperations.ZeroMemory(okm);
        }
    }

    /// <summary>
    /// 展示形式:material = email.ToLowerInvariant(),5 词以 '-' 连接。
    /// 与 bitwarden getFingerprintPhrase(email.toLowerCase()).join("-") 一致,
    /// 使本端短语与请求设备显示的短语逐字相同。
    /// </summary>
    public string GetFingerprintPhrase(byte[] publicKeyDer, string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return string.Join('-', GetFingerprint(publicKeyDer, email.ToLowerInvariant()));
    }

    /// <summary>把 32 字节哈希切成 5 个 EFF wordlist 词(base-256 大端 → 逐次 mod/div 7776)。</summary>
    public static string[] HashPhrase(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        // bigInt.fromArray(hashArr, 256) 等价于大端无符号整数
        var num = new BigInteger(hash, isUnsigned: true, isBigEndian: true);
        var words = new string[PhraseWords];
        for (var i = 0; i < PhraseWords; i++)
        {
            num = BigInteger.DivRem(num, WordCount, out var remainder);
            words[i] = Words[(int)remainder];
        }
        return words;
    }

    private static string[] LoadWordList()
    {
        var asm = typeof(FingerprintService).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("eff_large_wordlist.txt", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var list = new List<string>(WordCount);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length != 0) list.Add(line);
        }
        if (list.Count != WordCount)
            throw new InvalidOperationException(
                $"EFF wordlist 词数应为 {WordCount},实际 {list.Count}。请确认 Resources/eff_large_wordlist.txt 已作为 EmbeddedResource 打包。");
        return list.ToArray();
    }
}
