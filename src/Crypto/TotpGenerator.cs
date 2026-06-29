using System.Security.Cryptography;

namespace Crypto;

/// <summary>
/// RFC 6238 TOTP generator (HMAC-SHA1, default 6-digit, 30-second period).
/// </summary>
public static class TotpGenerator
{
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
