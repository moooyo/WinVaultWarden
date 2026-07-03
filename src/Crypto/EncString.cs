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

        // 类型前缀用 TryParse:非数字/溢出的恶意前缀得到干净 FormatException(前缀原文便于诊断,绝不含密钥物料),
        // 而非 int.Parse 抛出的 OverflowException 越过上层 per-cipher 过滤器。
        var prefix = value[..dot];
        if (!int.TryParse(prefix, out var typeNum))
            throw new FormatException($"EncString encType 前缀非法: '{prefix}'");

        var type = (EncryptionType)typeNum;
        var payload = value[(dot + 1)..];
        var parts = payload.Split('|');

        // 按 encType 校验最少段数,段数不足时抛 FormatException(而非索引访问越界的 IndexOutOfRangeException),
        // 保证截断的恶意/异常 EncString 被上层 IsCipherDecryptException 过滤器捕获,不致崩溃整库同步。
        return type switch
        {
            EncryptionType.AesCbc256_B64 => parts.Length >= 2
                ? new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), null)
                : throw new FormatException($"EncString type {typeNum} 需要 2 段(iv|ct),实际 {parts.Length} 段"),
            EncryptionType.AesCbc128_HmacSha256_B64 or EncryptionType.AesCbc256_HmacSha256_B64 => parts.Length >= 3
                ? new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2]))
                : throw new FormatException($"EncString type {typeNum} 需要 3 段(iv|ct|mac),实际 {parts.Length} 段"),
            EncryptionType.Rsa2048_OaepSha256_B64 or EncryptionType.Rsa2048_OaepSha1_B64 => parts.Length >= 1
                ? new EncString(type, Array.Empty<byte>(), Convert.FromBase64String(parts[0]), null)
                : throw new FormatException($"EncString type {typeNum} 需要 1 段(data),实际 {parts.Length} 段"),
            _ => throw new FormatException($"未知 encType: {typeNum}"),
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
