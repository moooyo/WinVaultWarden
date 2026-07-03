using System.Security.Cryptography;

namespace Crypto;

/// <summary>
/// RFC 6238 TOTP generator (HMAC-SHA1, default 6-digit, 30-second period).
/// </summary>
public static class TotpGenerator
{
    /// <summary>
    /// 从 Bitwarden 的 Totp 字段生成当前验证码。该字段可能是裸 Base32 密钥,
    /// 也可能是 otpauth://totp/...?secret=XXX&amp;digits=&amp;period= URI。
    /// 解析成功时输出 6(或指定)位码并返回 true;空/坏密钥/坏 URI 返回 false——
    /// 调用方据此显示占位,**绝不回退成把原始种子当验证码显示/复制**(S9)。
    /// </summary>
    public static bool TryGenerate(string? totp, long unixSeconds, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(totp))
            return false;

        var secret = totp.Trim();
        int digits = 6, period = 30;

        if (secret.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)
            && !TryParseOtpauth(secret, out secret, out digits, out period))
            return false;

        try
        {
            code = Generate(secret, unixSeconds, digits, period);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    // 从 otpauth URI 的查询串提取 secret / digits / period。缺 secret 或参数越界 → false。
    private static bool TryParseOtpauth(string uri, out string secret, out int digits, out int period)
    {
        secret = string.Empty;
        digits = 6;
        period = 30;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return false;

        foreach (var part in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;
            var key = Uri.UnescapeDataString(kv[0]);
            var val = Uri.UnescapeDataString(kv[1]);
            if (key.Equals("secret", StringComparison.OrdinalIgnoreCase))
                secret = val;
            else if (key.Equals("digits", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var d))
                digits = d;
            else if (key.Equals("period", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var p))
                period = p;
        }

        return !string.IsNullOrEmpty(secret) && digits is >= 1 and <= 9 && period > 0;
    }

    public static string Generate(string base32Secret, long unixSeconds, int digits = 6, int period = 30)
    {
        ArgumentNullException.ThrowIfNull(base32Secret);
        if (digits < 1 || digits > 9)
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "digits must be between 1 and 9.");

        var key = Base32Decode(base32Secret);
        var counter = unixSeconds / period;

        // Encode counter as big-endian 8-byte message
        var msg = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        var hash = HMACSHA1.HashData(key, msg);

        // Dynamic truncation (RFC 4226 §5.4)
        int off = hash[^1] & 0x0F;
        int bin = ((hash[off] & 0x7F) << 24)
                | (hash[off + 1] << 16)
                | (hash[off + 2] << 8)
                | hash[off + 3];

        // Use integer power (loop) to avoid floating-point imprecision from Math.Pow
        int modulus = 1;
        for (int i = 0; i < digits; i++)
            modulus *= 10;

        int otp = bin % modulus;
        return otp.ToString().PadLeft(digits, '0');
    }

    private static byte[] Base32Decode(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.TrimEnd('=').ToUpperInvariant();

        int bits = 0, val = 0;
        var output = new List<byte>();
        foreach (var c in s)
        {
            // Skip whitespace (e.g. user-copied secrets with spaces)
            if (char.IsWhiteSpace(c))
                continue;

            int idx = Alphabet.IndexOf(c);
            if (idx == -1)
                throw new FormatException($"Invalid Base32 character: '{c}'.");

            val = (val << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((val >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
