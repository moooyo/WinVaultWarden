using System.Formats.Cbor;
using System.Security.Cryptography;

namespace Crypto.V2;

public enum CoseContentFormat
{
    Utf8,
    Pkcs8PrivateKey,
    SpkiPublicKey,
    CoseKey,
    BitwardenLegacyKey,
    OctetStream,
    Cbor,
}

public enum BitwardenSigningNamespace
{
    SignedPublicKey = 1,
    SecurityState = 2,
}

internal static class CosePrimitives
{
    internal const long Xaes256GcmAlgorithm = -70010;
    internal const long Mldsa44Algorithm = -48;
    internal const long EdDsaAlgorithm = -8;
    internal const long SigningNamespaceLabel = -80000;
    internal const int CborContentFormat = 60;
    internal const int CoseKeyContentFormat = 101;
    internal const int Pkcs8ContentFormat = 284;
    internal const int OctetStreamContentFormat = 42;
    internal const int MaximumEncodedObjectLength = 1024 * 1024;

    private const string PaddedUtf8ContentType = "application/x.bitwarden.utf8-padded";
    private const string SpkiPublicKeyContentType = "application/x.bitwarden.spki-public-key";
    private const string BitwardenLegacyKeyContentType = "application/x.bitwarden.legacy-key";

    internal static byte[] EncodeEncrypt0ProtectedHeader(
        CoseContentFormat contentFormat,
        ReadOnlySpan<byte> keyId)
    {
        if (keyId.Length != V2UserKey.KeyIdSize)
            throw new ArgumentException("COSE key identifiers must be exactly 16 bytes.", nameof(keyId));

        var writer = NewCanonicalWriter();
        writer.WriteStartMap(3);
        writer.WriteInt32(1);
        writer.WriteInt64(Xaes256GcmAlgorithm);
        writer.WriteInt32(3);
        WriteContentType(writer, contentFormat);
        writer.WriteInt32(4);
        writer.WriteByteString(keyId);
        writer.WriteEndMap();
        return writer.Encode();
    }

    internal static byte[] EncodeEncrypt0Aad(ReadOnlySpan<byte> protectedHeader)
    {
        var writer = NewCanonicalWriter();
        writer.WriteStartArray(3);
        writer.WriteTextString("Encrypt0");
        writer.WriteByteString(protectedHeader);
        writer.WriteByteString(ReadOnlySpan<byte>.Empty);
        writer.WriteEndArray();
        return writer.Encode();
    }

    internal static byte[] EncodeSign1ProtectedHeader(
        long algorithm,
        ReadOnlySpan<byte> keyId,
        BitwardenSigningNamespace signingNamespace)
    {
        if (keyId.Length != V2UserKey.KeyIdSize)
            throw new ArgumentException("COSE key identifiers must be exactly 16 bytes.", nameof(keyId));

        var writer = NewCanonicalWriter();
        writer.WriteStartMap(4);
        writer.WriteInt32(1);
        writer.WriteInt64(algorithm);
        writer.WriteInt32(3);
        writer.WriteInt32(CborContentFormat);
        writer.WriteInt32(4);
        writer.WriteByteString(keyId);
        writer.WriteInt64(SigningNamespaceLabel);
        writer.WriteInt32((int)signingNamespace);
        writer.WriteEndMap();
        return writer.Encode();
    }

    internal static byte[] EncodeSign1ToBeSigned(
        ReadOnlySpan<byte> protectedHeader,
        ReadOnlySpan<byte> payload)
    {
        var writer = NewCanonicalWriter();
        writer.WriteStartArray(4);
        writer.WriteTextString("Signature1");
        writer.WriteByteString(protectedHeader);
        writer.WriteByteString(ReadOnlySpan<byte>.Empty);
        writer.WriteByteString(payload);
        writer.WriteEndArray();
        return writer.Encode();
    }

    internal static byte[] EncodeCoseEncrypt0(
        ReadOnlySpan<byte> protectedHeader,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextAndTag)
    {
        var writer = NewCanonicalWriter();
        writer.WriteStartArray(3);
        writer.WriteByteString(protectedHeader);
        writer.WriteStartMap(1);
        writer.WriteInt32(5);
        writer.WriteByteString(nonce);
        writer.WriteEndMap();
        writer.WriteByteString(ciphertextAndTag);
        writer.WriteEndArray();
        return writer.Encode();
    }

    internal static byte[] EncodeCoseSign1(
        ReadOnlySpan<byte> protectedHeader,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> signature)
    {
        var writer = NewCanonicalWriter();
        writer.WriteStartArray(4);
        writer.WriteByteString(protectedHeader);
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteByteString(payload);
        writer.WriteByteString(signature);
        writer.WriteEndArray();
        return writer.Encode();
    }

    internal static byte[] PadKeyForTransport(ReadOnlySpan<byte> coseKey)
    {
        const int minimumLength = 65;
        var paddingLength = Math.Max(minimumLength - coseKey.Length, 1);
        if (paddingLength > byte.MaxValue)
            throw new CryptographicException("COSE key transport padding exceeds one-byte length encoding.");

        var result = new byte[checked(coseKey.Length + paddingLength)];
        coseKey.CopyTo(result);
        result.AsSpan(coseKey.Length).Fill((byte)paddingLength);
        return result;
    }

    internal static byte[] UnpadTransportKey(ReadOnlySpan<byte> padded)
    {
        if (padded.Length <= 64)
            throw new CryptographicException("V2 user keys must use a padded COSE key encoding longer than 64 bytes.");
        return RemoveStrictPadding(padded, null);
    }

    internal static byte[] PadUtf8(ReadOnlySpan<byte> plaintext)
    {
        var paddedLength = checked(32 * (1 + (plaintext.Length / 32)));
        var result = new byte[paddedLength];
        plaintext.CopyTo(result);
        result.AsSpan(plaintext.Length).Fill((byte)(paddedLength - plaintext.Length));
        return result;
    }

    internal static byte[] UnpadUtf8(ReadOnlySpan<byte> plaintext)
        => RemoveStrictPadding(plaintext, 32);

    internal static byte[] DecodeBase64Exact(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaximumEncodedObjectLength * 2)
            throw new FormatException("Base64 value exceeds the protocol size limit.");

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new FormatException("Value is not canonical RFC 4648 Base64.", exception);
        }

        if (!string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new FormatException("Value is not canonical RFC 4648 Base64.");
        }

        return decoded;
    }

    internal static string ToLowerHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();

    internal static CborWriter NewCanonicalWriter()
        => new(CborConformanceMode.Canonical, convertIndefiniteLengthEncodings: true);

    internal static CborReader NewStrictReader(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length == 0 || encoded.Length > MaximumEncodedObjectLength)
            throw new FormatException("CBOR value is empty or exceeds the protocol size limit.");
        return new CborReader(encoded, CborConformanceMode.Lax, allowMultipleRootLevelValues: false);
    }

    internal static CborReader NewCanonicalReader(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.Length == 0 || encoded.Length > MaximumEncodedObjectLength)
            throw new FormatException("CBOR value is empty or exceeds the protocol size limit.");
        return new CborReader(
            encoded,
            CborConformanceMode.Canonical,
            allowMultipleRootLevelValues: false);
    }

    internal static void EnsureFinished(CborReader reader)
    {
        if (reader.BytesRemaining != 0 || reader.PeekState() != CborReaderState.Finished)
            throw new FormatException("CBOR value contains trailing data.");
    }

    internal static CoseContentFormat ReadContentType(CborReader reader)
    {
        return reader.PeekState() switch
        {
            CborReaderState.UnsignedInteger => FromNumericContentType(checked((int)reader.ReadUInt64())),
            CborReaderState.TextString => FromTextContentType(reader.ReadTextString()),
            _ => throw new FormatException("COSE content type must be an integer or text string."),
        };
    }

    internal static void WriteContentType(CborWriter writer, CoseContentFormat contentFormat)
    {
        switch (contentFormat)
        {
            case CoseContentFormat.Utf8:
                writer.WriteTextString(PaddedUtf8ContentType);
                break;
            case CoseContentFormat.Pkcs8PrivateKey:
                writer.WriteInt32(Pkcs8ContentFormat);
                break;
            case CoseContentFormat.SpkiPublicKey:
                writer.WriteTextString(SpkiPublicKeyContentType);
                break;
            case CoseContentFormat.CoseKey:
                writer.WriteInt32(CoseKeyContentFormat);
                break;
            case CoseContentFormat.BitwardenLegacyKey:
                writer.WriteTextString(BitwardenLegacyKeyContentType);
                break;
            case CoseContentFormat.OctetStream:
                writer.WriteInt32(OctetStreamContentFormat);
                break;
            case CoseContentFormat.Cbor:
                writer.WriteInt32(CborContentFormat);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contentFormat));
        }
    }

    private static CoseContentFormat FromNumericContentType(int value)
        => value switch
        {
            Pkcs8ContentFormat => CoseContentFormat.Pkcs8PrivateKey,
            CoseKeyContentFormat => CoseContentFormat.CoseKey,
            OctetStreamContentFormat => CoseContentFormat.OctetStream,
            CborContentFormat => CoseContentFormat.Cbor,
            _ => throw new FormatException($"Unsupported COSE numeric content type: {value}."),
        };

    private static CoseContentFormat FromTextContentType(string value)
        => value switch
        {
            PaddedUtf8ContentType => CoseContentFormat.Utf8,
            SpkiPublicKeyContentType => CoseContentFormat.SpkiPublicKey,
            BitwardenLegacyKeyContentType => CoseContentFormat.BitwardenLegacyKey,
            _ => throw new FormatException("Unsupported COSE text content type."),
        };

    private static byte[] RemoveStrictPadding(ReadOnlySpan<byte> padded, int? requiredBlockSize)
    {
        if (padded.IsEmpty)
            throw new CryptographicException("Padded value is empty.");
        if (requiredBlockSize is int blockSize && padded.Length % blockSize != 0)
            throw new CryptographicException("Padded value is not aligned to the required block size.");

        var paddingLength = padded[^1];
        if (paddingLength == 0 || paddingLength > padded.Length)
            throw new CryptographicException("Padding length is invalid.");

        var padding = padded[^paddingLength..];
        var mismatch = 0;
        foreach (var value in padding)
            mismatch |= value ^ paddingLength;
        if (mismatch != 0)
            throw new CryptographicException("Padding bytes are invalid.");

        return padded[..^paddingLength].ToArray();
    }
}
