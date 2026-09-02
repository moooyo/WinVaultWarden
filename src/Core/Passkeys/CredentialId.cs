using System.Security.Cryptography;

namespace Core.Passkeys;

public static class CredentialId
{
    public const string Base64Prefix = "b64.";

    public static string EncodeForStorage(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("A credential ID must not be empty.", nameof(value));

        return Base64Prefix + EncodeForWire(value);
    }

    public static string EncodeForWire(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            throw new ArgumentException("A credential ID must not be empty.", nameof(value));

        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] DecodeStorage(string value) =>
        TryDecodeStorage(value, out var bytes, out _)
            ? bytes
            : throw new FormatException("The stored credential ID is invalid.");

    public static bool TryDecodeStorage(
        string? value,
        out byte[] bytes,
        out bool wasLegacyBase64Url)
    {
        bytes = [];
        wasLegacyBase64Url = false;

        if (string.IsNullOrEmpty(value))
            return false;

        if (value.StartsWith(Base64Prefix, StringComparison.Ordinal))
            return TryDecodeCanonicalBase64Url(value[Base64Prefix.Length..], out bytes);

        if (TryDecodeGuid(value, out bytes))
            return true;

        if (!TryDecodeCanonicalBase64Url(value, out bytes))
            return false;

        wasLegacyBase64Url = true;
        return true;
    }

    public static byte[] DecodeWire(string value) =>
        TryDecodeWire(value, out var bytes)
            ? bytes
            : throw new FormatException("The WebAuthn credential ID is not canonical base64url.");

    public static bool TryDecodeWire(string? value, out byte[] bytes) =>
        TryDecodeCanonicalBase64Url(value, out bytes);

    public static string NormalizeStorage(string value)
    {
        if (!TryDecodeStorage(value, out var bytes, out var wasLegacyBase64Url))
            throw new FormatException("The stored credential ID is invalid.");

        if (wasLegacyBase64Url)
            return EncodeForStorage(bytes);

        return value.StartsWith(Base64Prefix, StringComparison.Ordinal)
            ? value
            : FormatGuid(bytes);
    }

    public static bool TryNormalizeStorage(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryDecodeStorage(value, out var bytes, out var wasLegacyBase64Url))
            return false;

        normalized = wasLegacyBase64Url
            ? EncodeForStorage(bytes)
            : value!.StartsWith(Base64Prefix, StringComparison.Ordinal)
                ? value
                : FormatGuid(bytes);
        return true;
    }

    public static bool StorageIdsEqual(string? left, string? right)
    {
        if (!TryDecodeStorage(left, out var leftBytes, out _)
            || !TryDecodeStorage(right, out var rightBytes, out _))
        {
            return false;
        }

        return FixedTimeEquals(leftBytes, rightBytes);
    }

    public static bool StorageIdEqualsWire(string? stored, string? wire)
    {
        if (!TryDecodeStorage(stored, out var storedBytes, out _)
            || !TryDecodeWire(wire, out var wireBytes))
        {
            return false;
        }

        return FixedTimeEquals(storedBytes, wireBytes);
    }

    public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private static bool TryDecodeCanonicalBase64Url(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length % 4 == 1)
            return false;

        foreach (var character in value)
        {
            if (!IsBase64UrlCharacter(character))
                return false;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (bytes.Length == 0 || !string.Equals(EncodeForWire(bytes), value, StringComparison.Ordinal))
        {
            bytes = [];
            return false;
        }

        return true;
    }

    private static bool TryDecodeGuid(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length != 36
            || value[8] != '-'
            || value[13] != '-'
            || value[18] != '-'
            || value[23] != '-')
        {
            return false;
        }

        var result = new byte[16];
        var sourceIndex = 0;
        var destinationIndex = 0;
        while (sourceIndex < value.Length)
        {
            if (sourceIndex is 8 or 13 or 18 or 23)
            {
                sourceIndex++;
                continue;
            }

            var high = HexValue(value[sourceIndex]);
            var low = HexValue(value[sourceIndex + 1]);
            if (high < 0 || low < 0)
                return false;

            result[destinationIndex++] = (byte)((high << 4) | low);
            sourceIndex += 2;
        }

        bytes = result;
        return true;
    }

    private static string FormatGuid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("A GUID credential ID must contain 16 bytes.", nameof(bytes));

        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static bool IsBase64UrlCharacter(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-'
            or '_';

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
}
