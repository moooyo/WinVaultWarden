using System.Security.Cryptography;

namespace Crypto;

/// <summary>
/// TOTP 哈希算法族(RFC 6238 支持 SHA1/SHA256/SHA512)。缺省 SHA1。
/// </summary>
public enum TotpAlgorithm
{
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// RFC 6238 TOTP generator (HMAC-SHA1/256/512, default 6-digit, 30-second period),
/// plus Bitwarden Steam Guard (steam:// 前缀,5 位 base-26 编码)。
/// </summary>
public static class TotpGenerator
{
    // Bitwarden steamguard 字母表(base-26,剔除易混字符)。
    private const string SteamAlphabet = "23456789BCDFGHJKMNPQRTVWXY";

    /// <summary>
    /// 从 Bitwarden 的 Totp 字段生成当前验证码。该字段可能是:
    ///   - 裸 Base32 密钥(默认 SHA1 / 6 位 / 30 秒);
    ///   - otpauth://totp/...?secret=XXX&amp;algorithm=SHA1|SHA256|SHA512&amp;digits=&amp;period= URI;
    ///   - steam://&lt;Base32&gt; Steam Guard 令牌(SHA1 / 5 位 / base-26)。
    /// 解析成功时输出验证码并返回 true;空/坏密钥/坏 URI 返回 false——
    /// 调用方据此显示占位,**绝不回退成把原始种子当验证码显示/复制**(S9)。
    /// </summary>
    public static bool TryGenerate(string? totp, long unixSeconds, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(totp))
            return false;

        var raw = totp.Trim();

        // Steam Guard:steam://<Base32> —— SHA1、5 位、base-26 编码。
        if (raw.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
        {
            var steamSecret = raw["steam://".Length..];
            if (string.IsNullOrWhiteSpace(steamSecret))
                return false;
            try
            {
                code = GenerateSteam(steamSecret, unixSeconds);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                return false;
            }
        }

        var secret = raw;
        int digits = 6, period = 30;
        var algorithm = TotpAlgorithm.Sha1;

        if (secret.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)
            && !TryParseOtpauth(secret, out secret, out digits, out period, out algorithm))
            return false;

        try
        {
            code = Generate(secret, unixSeconds, digits, period, algorithm);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    // 从 otpauth URI 的查询串提取 secret / algorithm / digits / period。缺 secret 或参数越界 → false。
    // algorithm 缺省或无法识别时回退 SHA1(RFC 6238 默认)。
    private static bool TryParseOtpauth(string uri, out string secret, out int digits, out int period, out TotpAlgorithm algorithm)
    {
        secret = string.Empty;
        digits = 6;
        period = 30;
        algorithm = TotpAlgorithm.Sha1;

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
            else if (key.Equals("algorithm", StringComparison.OrdinalIgnoreCase))
                algorithm = val.ToUpperInvariant() switch
                {
                    "SHA256" => TotpAlgorithm.Sha256,
                    "SHA512" => TotpAlgorithm.Sha512,
                    _ => TotpAlgorithm.Sha1,
                };
        }

        return !string.IsNullOrEmpty(secret) && digits is >= 1 and <= 9 && period > 0;
    }

    public static string Generate(string base32Secret, long unixSeconds, int digits = 6, int period = 30, TotpAlgorithm algorithm = TotpAlgorithm.Sha1)
    {
        ArgumentNullException.ThrowIfNull(base32Secret);
        if (digits < 1 || digits > 9)
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "digits must be between 1 and 9.");

        var key = Base32Decode(base32Secret);
        int bin = ComputeTruncatedBinary(key, unixSeconds, period, algorithm);

        // Use integer power (loop) to avoid floating-point imprecision from Math.Pow
        int modulus = 1;
        for (int i = 0; i < digits; i++)
            modulus *= 10;

        int otp = bin % modulus;
        return otp.ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// Steam Guard 码:HMAC-SHA1 动态截断后按 5 字符 base-26 字母表编码(Bitwarden steamguard)。
    /// </summary>
    public static string GenerateSteam(string base32Secret, long unixSeconds, int period = 30)
    {
        ArgumentNullException.ThrowIfNull(base32Secret);

        var key = Base32Decode(base32Secret);
        int bin = ComputeTruncatedBinary(key, unixSeconds, period, TotpAlgorithm.Sha1);

        var chars = new char[5];
        for (int i = 0; i < 5; i++)
        {
            chars[i] = SteamAlphabet[bin % SteamAlphabet.Length];
            bin /= SteamAlphabet.Length;
        }
        return new string(chars);
    }

    // RFC 4226 §5.4 动态截断:算出 31 位无符号整数,供十进制取模或 Steam base-26 编码复用。
    private static int ComputeTruncatedBinary(byte[] key, long unixSeconds, int period, TotpAlgorithm algorithm)
    {
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period), period, "period must be positive.");

        var counter = unixSeconds / period;

        // Encode counter as big-endian 8-byte message
        var msg = new byte[8];
        for (int i = 7; i >= 0; i--)
        {
            msg[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        byte[] hash = algorithm switch
        {
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(key, msg),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(key, msg),
            _ => HMACSHA1.HashData(key, msg),
        };

        int off = hash[^1] & 0x0F;
        return ((hash[off] & 0x7F) << 24)
             | (hash[off + 1] << 16)
             | (hash[off + 2] << 8)
             | hash[off + 3];
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
