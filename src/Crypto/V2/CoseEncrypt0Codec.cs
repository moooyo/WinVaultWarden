using System.Formats.Cbor;
using System.Security.Cryptography;

namespace Crypto.V2;

public sealed class CoseEncrypt0Envelope
{
    private readonly byte[] _keyId;
    private readonly byte[] _nonce;
    private readonly byte[] _protectedHeader;
    private readonly byte[] _ciphertextAndTag;

    internal CoseEncrypt0Envelope(
        CoseContentFormat contentFormat,
        byte[] keyId,
        byte[] nonce,
        byte[] protectedHeader,
        byte[] ciphertextAndTag)
    {
        ContentFormat = contentFormat;
        _keyId = keyId;
        _nonce = nonce;
        _protectedHeader = protectedHeader;
        _ciphertextAndTag = ciphertextAndTag;
    }

    public CoseContentFormat ContentFormat { get; }
    public string KeyIdHex => CosePrimitives.ToLowerHex(_keyId);
    public ReadOnlyMemory<byte> KeyId => _keyId;
    public ReadOnlyMemory<byte> Nonce => _nonce;
    public ReadOnlyMemory<byte> ProtectedHeader => _protectedHeader;
    public ReadOnlyMemory<byte> CiphertextAndTag => _ciphertextAndTag;
}

/// <summary>
/// Encodes and decodes Bitwarden type-7 EncStrings containing untagged COSE_Encrypt0 messages.
/// </summary>
public static class CoseEncrypt0Codec
{
    public const int EncStringType = 7;
    public const long Xaes256GcmAlgorithm = CosePrimitives.Xaes256GcmAlgorithm;

    public static string EncryptToEncString(
        ReadOnlySpan<byte> plaintext,
        V2UserKey key,
        CoseContentFormat contentFormat)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.ValidateEncryptOperation();

        var protectedHeader = CosePrimitives.EncodeEncrypt0ProtectedHeader(contentFormat, key.KeyIdBytes);
        var associatedData = CosePrimitives.EncodeEncrypt0Aad(protectedHeader);
        var nonce = RandomNumberGenerator.GetBytes(Xaes256Gcm.NonceSize);
        var input = contentFormat == CoseContentFormat.Utf8
            ? CosePrimitives.PadUtf8(plaintext)
            : plaintext.ToArray();
        byte[]? ciphertextAndTag = null;
        byte[]? encoded = null;

        try
        {
            ciphertextAndTag = Xaes256Gcm.Encrypt(key.KeyBytes, nonce, input, associatedData);
            encoded = CosePrimitives.EncodeCoseEncrypt0(protectedHeader, nonce, ciphertextAndTag);
            return $"{EncStringType}.{Convert.ToBase64String(encoded)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(protectedHeader);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(nonce);
            if (ciphertextAndTag is not null)
                CryptographicOperations.ZeroMemory(ciphertextAndTag);
            if (encoded is not null)
                CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public static byte[] DecryptEncString(
        string value,
        V2UserKey key,
        CoseContentFormat expectedContentFormat)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.ValidateDecryptOperation();

        var envelope = ParseEncString(value);
        if (envelope.ContentFormat != expectedContentFormat)
            throw new CryptographicException("COSE content type does not match the expected payload type.");
        if (!CryptographicOperations.FixedTimeEquals(envelope.KeyId.Span, key.KeyIdBytes))
            throw new CryptographicException("COSE key identifier does not match the supplied user key.");

        var associatedData = CosePrimitives.EncodeEncrypt0Aad(envelope.ProtectedHeader.Span);
        try
        {
            var decrypted = Xaes256Gcm.Decrypt(
                key.KeyBytes,
                envelope.Nonce.Span,
                envelope.CiphertextAndTag.Span,
                associatedData);

            if (expectedContentFormat != CoseContentFormat.Utf8)
                return decrypted;

            try
            {
                return CosePrimitives.UnpadUtf8(decrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public static CoseEncrypt0Envelope ParseEncString(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const string prefix = "7.";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)
            || value.IndexOf('.', prefix.Length) >= 0
            || value.Contains('|', StringComparison.Ordinal))
        {
            throw new FormatException("Value is not a canonical Bitwarden type-7 EncString.");
        }

        var encoded = CosePrimitives.DecodeBase64Exact(value[prefix.Length..], nameof(value));
        try
        {
            return ParseCoseEncrypt0(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static CoseEncrypt0Envelope ParseCoseEncrypt0(ReadOnlyMemory<byte> encoded)
    {
        var reader = CosePrimitives.NewCanonicalReader(encoded);
        if (reader.ReadStartArray() != 3)
            throw new FormatException("COSE_Encrypt0 must be a definite three-element array.");

        var protectedHeader = reader.ReadByteString();
        if (protectedHeader.Length == 0)
            throw new FormatException("COSE_Encrypt0 protected header is empty.");

        if (reader.ReadStartMap() != 1 || reader.ReadInt64() != 5)
            throw new FormatException("COSE_Encrypt0 unprotected header must contain only the IV.");
        var nonce = reader.ReadByteString();
        reader.ReadEndMap();
        if (nonce.Length != Xaes256Gcm.NonceSize)
            throw new FormatException("COSE_Encrypt0 XAES nonce must be exactly 24 bytes.");

        var ciphertextAndTag = reader.ReadByteString();
        if (ciphertextAndTag.Length < Xaes256Gcm.TagSize)
            throw new FormatException("COSE_Encrypt0 ciphertext is shorter than its authentication tag.");
        reader.ReadEndArray();
        CosePrimitives.EnsureFinished(reader);

        byte[]? keyId = null;
        try
        {
            var contentFormat = ParseProtectedHeader(protectedHeader, out keyId);
            return new CoseEncrypt0Envelope(contentFormat, keyId, nonce, protectedHeader, ciphertextAndTag);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(protectedHeader);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertextAndTag);
            if (keyId is not null)
                CryptographicOperations.ZeroMemory(keyId);
            throw;
        }
    }

    private static CoseContentFormat ParseProtectedHeader(
        ReadOnlyMemory<byte> encoded,
        out byte[] keyId)
    {
        var reader = CosePrimitives.NewCanonicalReader(encoded);
        if (reader.ReadStartMap() != 3)
            throw new FormatException("COSE_Encrypt0 protected header must contain exactly three parameters.");

        long? algorithm = null;
        CoseContentFormat? contentFormat = null;
        byte[]? parsedKeyId = null;
        var seen = new HashSet<long>();

        try
        {
            for (var index = 0; index < 3; index++)
            {
                var label = reader.ReadInt64();
                if (!seen.Add(label))
                    throw new FormatException("COSE_Encrypt0 protected header contains a duplicate parameter.");

                switch (label)
                {
                    case 1:
                        algorithm = reader.ReadInt64();
                        break;
                    case 3:
                        contentFormat = CosePrimitives.ReadContentType(reader);
                        break;
                    case 4:
                        parsedKeyId = reader.ReadByteString();
                        break;
                    default:
                        throw new FormatException($"Unsupported COSE_Encrypt0 protected parameter: {label}.");
                }
            }

            reader.ReadEndMap();
            CosePrimitives.EnsureFinished(reader);
            if (algorithm != Xaes256GcmAlgorithm
                || contentFormat is null
                || parsedKeyId is not { Length: V2UserKey.KeyIdSize })
            {
                throw new FormatException("COSE_Encrypt0 protected header is invalid for Bitwarden XAES-256-GCM.");
            }

            var canonical = CosePrimitives.EncodeEncrypt0ProtectedHeader(contentFormat.Value, parsedKeyId);
            try
            {
                if (!encoded.Span.SequenceEqual(canonical))
                    throw new FormatException("COSE_Encrypt0 protected header is not canonically encoded.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }

            keyId = parsedKeyId;
            parsedKeyId = null;
            return contentFormat.Value;
        }
        finally
        {
            if (parsedKeyId is not null)
                CryptographicOperations.ZeroMemory(parsedKeyId);
        }
    }
}
