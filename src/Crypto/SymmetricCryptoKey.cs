namespace Crypto;

// 对称密钥封装:32 字节(仅 EncKey)或 64 字节(EncKey 32 ‖ MacKey 32)。
public sealed class SymmetricCryptoKey
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
}
