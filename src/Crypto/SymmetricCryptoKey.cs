using System.Security.Cryptography;

namespace Crypto;

// 对称密钥封装:32 字节(仅 EncKey)或 64 字节(EncKey 32 ‖ MacKey 32)。
public sealed class SymmetricCryptoKey : IDisposable
{
    public byte[] FullKey { get; }
    public byte[] EncKey { get; }
    public byte[]? MacKey { get; }

    public SymmetricCryptoKey(byte[] keyBytes)
    {
        FullKey = keyBytes;
        switch (keyBytes.Length)
        {
            case 32:
                EncKey = keyBytes;
                MacKey = null;
                break;
            case 64:
                EncKey = keyBytes[..32];
                MacKey = keyBytes[32..];
                break;
            default:
                throw new ArgumentException($"密钥长度须为 32 或 64,实际 {keyBytes.Length}");
        }
    }

    public SymmetricCryptoKey(byte[] encKey, byte[] macKey)
    {
        EncKey = encKey;
        MacKey = macKey;
        FullKey = [.. encKey, .. macKey];
    }

    // 清零全部支撑数组(FullKey、EncKey、非 null 的 MacKey),抹去内存中的密钥物料。
    // 不同构造下这三者可能别名同一数组(32 字节构造:FullKey==EncKey);
    // ZeroMemory 幂等,对别名数组重复清零无害,故无需去重。
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(FullKey);
        CryptographicOperations.ZeroMemory(EncKey);
        if (MacKey is not null)
            CryptographicOperations.ZeroMemory(MacKey);
    }
}
