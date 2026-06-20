using Core.Enums;

namespace Crypto;

// 解析 Bitwarden EncString:"<encType>.<base64 段...>"
// type 0:iv|ct;type 1/2:iv|ct|mac;type 3/4(RSA):data(单段)。
public sealed class EncString
{
    public EncryptionType Type { get; }
    public byte[] Iv { get; }
    public byte[] Ct { get; }
    public byte[]? Mac { get; }

    public EncString(EncryptionType type, byte[] iv, byte[] ct, byte[]? mac)
    {
        Type = type;
        Iv = iv;
        Ct = ct;
        Mac = mac;
    }

    public static EncString Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new FormatException("EncString 为空");

        var dot = value.IndexOf('.');
        if (dot < 0)
            throw new FormatException("EncString 缺少 encType 前缀");

        var type = (EncryptionType)int.Parse(value[..dot]);
        var payload = value[(dot + 1)..];
        var parts = payload.Split('|');

        return type switch
        {
            EncryptionType.AesCbc256_B64 =>
                new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), null),
            EncryptionType.AesCbc128_HmacSha256_B64 or EncryptionType.AesCbc256_HmacSha256_B64 =>
                new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2])),
            EncryptionType.Rsa2048_OaepSha256_B64 or EncryptionType.Rsa2048_OaepSha1_B64 =>
                new EncString(type, Array.Empty<byte>(), Convert.FromBase64String(parts[0]), null),
            _ => throw new FormatException($"未知 encType: {(int)type}"),
        };
    }

    public override string ToString()
    {
        var t = (int)Type;
        return Type switch
        {
            EncryptionType.AesCbc256_B64 =>
                $"{t}.{Convert.ToBase64String(Iv)}|{Convert.ToBase64String(Ct)}",
            EncryptionType.AesCbc128_HmacSha256_B64 or EncryptionType.AesCbc256_HmacSha256_B64 =>
                $"{t}.{Convert.ToBase64String(Iv)}|{Convert.ToBase64String(Ct)}|{Convert.ToBase64String(Mac!)}",
            EncryptionType.Rsa2048_OaepSha256_B64 or EncryptionType.Rsa2048_OaepSha1_B64 =>
                $"{t}.{Convert.ToBase64String(Ct)}",
            _ => throw new InvalidOperationException(),
        };
    }
}
