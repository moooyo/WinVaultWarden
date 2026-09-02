using Core.Enums;

namespace Crypto;

// Parses Bitwarden EncString values: "<encType>.<base64 segments>".
public sealed class EncString
{
    public const int LegacyRsaMacLength = 32;

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
            throw new FormatException("EncString is empty.");

        var dot = value.IndexOf('.');
        if (dot < 0)
            throw new FormatException("EncString is missing its encryption type prefix.");

        // TryParse keeps malformed and overflowing prefixes on the FormatException path.
        var prefix = value[..dot];
        if (!int.TryParse(prefix, out var typeNum))
            throw new FormatException($"Invalid EncString encryption type prefix: '{prefix}'.");

        var type = (EncryptionType)typeNum;
        var payload = value[(dot + 1)..];
        var parts = payload.Split('|');

        return type switch
        {
            EncryptionType.AesCbc256_B64 => parts.Length == 2
                ? new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), null)
                : throw InvalidPartCount(typeNum, "iv|data", parts.Length),
            EncryptionType.AesCbc128_HmacSha256_B64 or EncryptionType.AesCbc256_HmacSha256_B64 => parts.Length == 3
                ? new EncString(type, Convert.FromBase64String(parts[0]), Convert.FromBase64String(parts[1]), Convert.FromBase64String(parts[2]))
                : throw InvalidPartCount(typeNum, "iv|data|mac", parts.Length),
            EncryptionType.Rsa2048_OaepSha256_B64 or EncryptionType.Rsa2048_OaepSha1_B64 => parts.Length == 1
                ? new EncString(type, Array.Empty<byte>(), Convert.FromBase64String(parts[0]), null)
                : throw InvalidPartCount(typeNum, "data", parts.Length),
            EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64
                or EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64 =>
                ParseLegacyRsaHmac(type, typeNum, parts),
            _ => throw new FormatException($"Unknown EncString encryption type: {typeNum}."),
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
            EncryptionType.Rsa2048_OaepSha256_HmacSha256_B64
                or EncryptionType.Rsa2048_OaepSha1_HmacSha256_B64 =>
                FormatLegacyRsaHmac(t),
            _ => throw new InvalidOperationException(),
        };
    }

    private static EncString ParseLegacyRsaHmac(
        EncryptionType type,
        int typeNumber,
        string[] parts)
    {
        if (parts.Length != 2)
            throw InvalidPartCount(typeNumber, "data|mac", parts.Length);

        var data = Convert.FromBase64String(parts[0]);
        var mac = Convert.FromBase64String(parts[1]);
        if (mac.Length != LegacyRsaMacLength)
        {
            throw new FormatException(
                $"EncString type {typeNumber} MAC must be exactly {LegacyRsaMacLength} bytes.");
        }

        return new EncString(type, Array.Empty<byte>(), data, mac);
    }

    private string FormatLegacyRsaHmac(int typeNumber)
    {
        if (Iv.Length != 0 || Mac is not { Length: LegacyRsaMacLength })
        {
            throw new InvalidOperationException(
                $"EncString type {typeNumber} has an invalid legacy RSA-HMAC structure.");
        }

        return $"{typeNumber}.{Convert.ToBase64String(Ct)}|{Convert.ToBase64String(Mac)}";
    }

    private static FormatException InvalidPartCount(
        int typeNumber,
        string expectedShape,
        int actualCount) =>
        new($"EncString type {typeNumber} must have exactly '{expectedShape}' segments; received {actualCount}.");
}
