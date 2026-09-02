using System.Formats.Cbor;
using System.Security.Cryptography;
using Crypto;
using Crypto.V2;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Vault;

internal static class CoseDataEnvelopeCodec
{
    private const long Aes256GcmAlgorithm = 3;
    private const long XChaCha20Poly1305Algorithm = -70000;
    private const long Xaes256GcmAlgorithm = -70010;
    private const long SafeObjectNamespaceLabel = -80002;
    private const long SafeContentNamespaceLabel = -80001;
    private const int DataEnvelopeNamespace = 2;
    private const int VaultItemNamespace = 1;
    private const int SymmetricKeyType = 4;
    private const int DecryptOperation = 4;
    private const int AesNonceSize = 12;
    private const int ExtendedNonceSize = 24;
    private const int TagSize = 16;
    private const int MinimumPaddedLength = 64;
    private const int MaximumEnvelopeBytes = 4 * 1024 * 1024;
    private const string PaddedCborContentType = "application/x.bitwarden.cbor-padded";

    internal static (string WrappedCek, string Envelope) Seal(
        ReadOnlySpan<byte> content,
        SymmetricCryptoKey itemKey,
        CryptoService crypto)
    {
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(crypto);
        if (itemKey.FullKey.Length != 64)
            throw new CryptographicException("Cipher item keys must be 64-byte AES-CBC-HMAC keys.");

        var cek = RandomNumberGenerator.GetBytes(32);
        var keyId = RandomNumberGenerator.GetBytes(16);
        byte[]? encodedCek = null;
        byte[]? transportCek = null;
        byte[]? protectedHeader = null;
        byte[]? aad = null;
        byte[]? paddedContent = null;
        byte[]? nonce = null;
        byte[]? ciphertextAndTag = null;
        byte[]? envelope = null;
        try
        {
            encodedCek = EncodeCoseKey(Aes256GcmAlgorithm, keyId, cek);
            transportCek = PadToMinimumKeyLength(encodedCek);
            var wrappedCek = crypto.Encrypt(transportCek, itemKey).ToString();

            protectedHeader = EncodeProtectedHeader(Aes256GcmAlgorithm, keyId);
            aad = EncodeAad(protectedHeader);
            paddedContent = Pad(content, MinimumPaddedLength);
            nonce = RandomNumberGenerator.GetBytes(AesNonceSize);
            ciphertextAndTag = EncryptAesGcm(cek, nonce, paddedContent, aad);
            envelope = EncodeEnvelope(protectedHeader, nonce, ciphertextAndTag);
            return (wrappedCek, Convert.ToBase64String(envelope));
        }
        finally
        {
            Zero(cek);
            Zero(keyId);
            Zero(encodedCek);
            Zero(transportCek);
            Zero(protectedHeader);
            Zero(aad);
            Zero(paddedContent);
            Zero(nonce);
            Zero(ciphertextAndTag);
            Zero(envelope);
        }
    }

    internal static byte[] Unseal(
        string wrappedCek,
        string envelopeText,
        SymmetricCryptoKey itemKey,
        CryptoService crypto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wrappedCek);
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeText);
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(crypto);

        var wrapped = EncString.Parse(wrappedCek);
        if (wrapped.Type != Core.Enums.EncryptionType.AesCbc256_HmacSha256_B64)
            throw new CryptographicException("Cipher blob CEKs must be wrapped by encryption type 2.");

        byte[]? cekTransport = null;
        try
        {
            cekTransport = crypto.Decrypt(wrapped, itemKey);
            return UnsealWithCek(cekTransport, envelopeText, VaultItemNamespace);
        }
        finally
        {
            Zero(cekTransport);
        }
    }

    internal static byte[] UnsealWithCek(
        ReadOnlySpan<byte> cekTransport,
        string envelopeText,
        int expectedContentNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envelopeText);
        using var cek = ParseCoseKey(cekTransport);
        using var envelope = ParseEnvelope(
            DecodeBase64Canonical(envelopeText),
            expectedContentNamespace);
        byte[]? aad = null;
        byte[]? padded = null;
        try
        {

            if (envelope.Algorithm != cek.Algorithm
                || !CryptographicOperations.FixedTimeEquals(envelope.KeyId, cek.KeyId))
            {
                throw new CryptographicException(
                    "Cipher blob envelope does not match its wrapped content-encryption key.");
            }

            aad = EncodeAad(envelope.ProtectedHeader);
            padded = envelope.Algorithm switch
            {
                Aes256GcmAlgorithm => DecryptAesGcm(
                    cek.Key, envelope.Nonce, envelope.CiphertextAndTag, aad),
                Xaes256GcmAlgorithm => Xaes256Gcm.Decrypt(
                    cek.Key, envelope.Nonce, envelope.CiphertextAndTag, aad),
                XChaCha20Poly1305Algorithm => DecryptXChaCha20Poly1305(
                    cek.Key, envelope.Nonce, envelope.CiphertextAndTag, aad),
                _ => throw new CryptographicException("Unsupported cipher blob CEK algorithm."),
            };
            return Unpad(padded, requiredBlockSize: null);
        }
        finally
        {
            Zero(aad);
            Zero(padded);
        }
    }

    private static byte[] EncodeCoseKey(
        long algorithm,
        ReadOnlySpan<byte> keyId,
        ReadOnlySpan<byte> key)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt32(SymmetricKeyType);
        writer.WriteInt32(2);
        writer.WriteByteString(keyId);
        writer.WriteInt32(3);
        writer.WriteInt64(algorithm);
        writer.WriteInt32(4);
        writer.WriteStartArray(1);
        writer.WriteInt32(DecryptOperation);
        writer.WriteEndArray();
        writer.WriteInt32(-1);
        writer.WriteByteString(key);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static CekMaterial ParseCoseKey(ReadOnlySpan<byte> transport)
    {
        var encoded = UnpadMinimumKey(transport);
        try
        {
            var reader = NewReader(encoded);
            if (reader.ReadStartMap() != 5)
                throw new FormatException("Cipher blob CEK must contain exactly five COSE parameters.");

            int? keyType = null;
            long? algorithm = null;
            byte[]? keyId = null;
            byte[]? key = null;
            var operationsValid = false;
            var seen = new HashSet<long>();
            try
            {
                for (var index = 0; index < 5; index++)
                {
                    var label = reader.ReadInt64();
                    if (!seen.Add(label))
                        throw new FormatException("Cipher blob CEK contains a duplicate COSE parameter.");
                    switch (label)
                    {
                        case 1:
                            keyType = checked((int)reader.ReadInt64());
                            break;
                        case 2:
                            keyId = reader.ReadByteString();
                            break;
                        case 3:
                            algorithm = reader.ReadInt64();
                            break;
                        case 4:
                            operationsValid = ReadDecryptOnlyOperations(reader);
                            break;
                        case -1:
                            key = reader.ReadByteString();
                            break;
                        default:
                            throw new FormatException("Cipher blob CEK contains an unknown COSE parameter.");
                    }
                }
                reader.ReadEndMap();
                EnsureFinished(reader);
                if (keyType != SymmetricKeyType
                    || algorithm is not (Aes256GcmAlgorithm
                        or Xaes256GcmAlgorithm
                        or XChaCha20Poly1305Algorithm)
                    || keyId is not { Length: 16 }
                    || key is not { Length: 32 }
                    || !operationsValid)
                {
                    throw new FormatException("Cipher blob CEK is not a supported decryption key.");
                }

                var result = new CekMaterial(algorithm.Value, keyId, key);
                keyId = null;
                key = null;
                return result;
            }
            finally
            {
                Zero(keyId);
                Zero(key);
            }
        }
        finally
        {
            Zero(encoded);
        }
    }

    private static bool ReadDecryptOnlyOperations(CborReader reader)
    {
        if (reader.ReadStartArray() != 1 || reader.ReadInt64() != DecryptOperation)
            return false;
        reader.ReadEndArray();
        return true;
    }

    private static byte[] EncodeProtectedHeader(long algorithm, ReadOnlySpan<byte> keyId)
    {
        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);
        writer.WriteInt64(algorithm);
        writer.WriteInt32(3);
        writer.WriteTextString(PaddedCborContentType);
        writer.WriteInt32(4);
        writer.WriteByteString(keyId);
        writer.WriteInt64(SafeObjectNamespaceLabel);
        writer.WriteInt32(DataEnvelopeNamespace);
        writer.WriteInt64(SafeContentNamespaceLabel);
        writer.WriteInt32(VaultItemNamespace);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static byte[] EncodeAad(ReadOnlySpan<byte> protectedHeader)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartArray(3);
        writer.WriteTextString("Encrypt0");
        writer.WriteByteString(protectedHeader);
        writer.WriteByteString(ReadOnlySpan<byte>.Empty);
        writer.WriteEndArray();
        return writer.Encode();
    }

    private static byte[] EncodeEnvelope(
        ReadOnlySpan<byte> protectedHeader,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextAndTag)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
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

    private static ParsedEnvelope ParseEnvelope(
        byte[] encoded,
        int expectedContentNamespace)
    {
        try
        {
            var reader = NewReader(encoded);
            if (reader.ReadStartArray() != 3)
                throw new FormatException("Cipher blob DataEnvelope must be a three-element COSE_Encrypt0.");
            var protectedHeader = reader.ReadByteString();
            if (protectedHeader.Length == 0)
                throw new FormatException("Cipher blob DataEnvelope has an empty protected header.");
            if (reader.ReadStartMap() != 1 || reader.ReadInt64() != 5)
                throw new FormatException("Cipher blob DataEnvelope unprotected header must contain only IV.");
            var nonce = reader.ReadByteString();
            reader.ReadEndMap();
            var ciphertextAndTag = reader.ReadByteString();
            reader.ReadEndArray();
            EnsureFinished(reader);

            byte[]? keyId = null;
            try
            {
                var algorithm = ParseProtectedHeader(
                    protectedHeader,
                    expectedContentNamespace,
                    out keyId);
                var expectedNonceLength = algorithm == Aes256GcmAlgorithm
                    ? AesNonceSize
                    : ExtendedNonceSize;
                if (nonce.Length != expectedNonceLength || ciphertextAndTag.Length < TagSize)
                    throw new FormatException("Cipher blob DataEnvelope has an invalid nonce or tag length.");
                var result = new ParsedEnvelope(
                    algorithm,
                    keyId,
                    nonce,
                    protectedHeader,
                    ciphertextAndTag);
                keyId = null;
                return result;
            }
            catch
            {
                Zero(protectedHeader);
                Zero(nonce);
                Zero(ciphertextAndTag);
                throw;
            }
            finally
            {
                Zero(keyId);
            }
        }
        finally
        {
            Zero(encoded);
        }
    }

    private static long ParseProtectedHeader(
        ReadOnlyMemory<byte> encoded,
        int expectedContentNamespace,
        out byte[] keyId)
    {
        var reader = NewReader(encoded);
        var mapLength = reader.ReadStartMap();
        if (mapLength is null or < 3 or > 5)
            throw new FormatException(
                "Cipher blob DataEnvelope protected header has an invalid size.");

        long? algorithm = null;
        string? contentType = null;
        byte[]? parsedKeyId = null;
        int? objectNamespace = null;
        int? contentNamespace = null;
        var seen = new HashSet<long>();
        try
        {
            for (var index = 0; index < mapLength.Value; index++)
            {
                var label = reader.ReadInt64();
                if (!seen.Add(label))
                    throw new FormatException(
                        "Cipher blob DataEnvelope protected header contains a duplicate parameter.");
                switch (label)
                {
                    case 1:
                        algorithm = reader.ReadInt64();
                        break;
                    case 3:
                        if (reader.PeekState() != CborReaderState.TextString)
                            throw new FormatException(
                                "Cipher blob DataEnvelope content type must be text.");
                        contentType = reader.ReadTextString();
                        break;
                    case 4:
                        parsedKeyId = reader.ReadByteString();
                        break;
                    case SafeObjectNamespaceLabel:
                        objectNamespace = checked((int)reader.ReadInt64());
                        break;
                    case SafeContentNamespaceLabel:
                        contentNamespace = checked((int)reader.ReadInt64());
                        break;
                    default:
                        throw new FormatException(
                            "Cipher blob DataEnvelope protected header contains an unknown parameter.");
                }
            }
            reader.ReadEndMap();
            EnsureFinished(reader);

            if (algorithm is not (Aes256GcmAlgorithm
                    or Xaes256GcmAlgorithm
                    or XChaCha20Poly1305Algorithm)
                || contentType != PaddedCborContentType
                || parsedKeyId is not { Length: 16 }
                || (objectNamespace is not null
                    && objectNamespace != DataEnvelopeNamespace)
                || (contentNamespace is not null
                    && contentNamespace != expectedContentNamespace))
            {
                throw new FormatException(
                    "Cipher blob DataEnvelope protected header is invalid.");
            }

            keyId = parsedKeyId;
            parsedKeyId = null;
            return algorithm.Value;
        }
        finally
        {
            Zero(parsedKeyId);
        }
    }

    private static byte[] EncryptAesGcm(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> aad)
    {
        var output = new byte[checked(plaintext.Length + TagSize)];
        using var cipher = new AesGcm(key, TagSize);
        cipher.Encrypt(
            nonce,
            plaintext,
            output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length, TagSize),
            aad);
        return output;
    }

    private static byte[] DecryptAesGcm(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextAndTag,
        ReadOnlySpan<byte> aad)
    {
        if (ciphertextAndTag.Length < TagSize)
            throw new CryptographicException("AES-GCM ciphertext is shorter than its tag.");
        var plaintext = new byte[ciphertextAndTag.Length - TagSize];
        try
        {
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(
                nonce,
                ciphertextAndTag[..^TagSize],
                ciphertextAndTag[^TagSize..],
                plaintext,
                aad);
            return plaintext;
        }
        catch
        {
            Zero(plaintext);
            throw;
        }
    }

    private static byte[] DecryptXChaCha20Poly1305(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextAndTag,
        ReadOnlySpan<byte> aad)
    {
        if (key.Length != 32 || nonce.Length != ExtendedNonceSize)
            throw new CryptographicException("XChaCha20-Poly1305 key or nonce length is invalid.");

        var keyBytes = key.ToArray();
        var nonceBytes = nonce.ToArray();
        var aadBytes = aad.ToArray();
        var ciphertextBytes = ciphertextAndTag.ToArray();
        byte[]? output = null;
        try
        {
            var cipher = new XChaCha20Poly1305();
            cipher.Init(
                false,
                new AeadParameters(
                    new KeyParameter(keyBytes),
                    TagSize * 8,
                    nonceBytes,
                    aadBytes));
            output = new byte[cipher.GetOutputSize(ciphertextBytes.Length)];
            var length = cipher.ProcessBytes(
                ciphertextBytes,
                0,
                ciphertextBytes.Length,
                output,
                0);
            length += cipher.DoFinal(output, length);
            if (length != output.Length)
                Array.Resize(ref output, length);
            return output;
        }
        catch (InvalidCipherTextException exception)
        {
            Zero(output);
            throw new CryptographicException(
                "XChaCha20-Poly1305 authentication failed.",
                exception);
        }
        catch
        {
            Zero(output);
            throw;
        }
        finally
        {
            Zero(keyBytes);
            Zero(nonceBytes);
            Zero(aadBytes);
            Zero(ciphertextBytes);
        }
    }

    private static byte[] PadToMinimumKeyLength(ReadOnlySpan<byte> value)
    {
        const int minimumLength = 65;
        var paddingLength = Math.Max(minimumLength - value.Length, 1);
        if (paddingLength > byte.MaxValue)
            throw new CryptographicException("COSE key transport padding is too large.");
        var result = new byte[checked(value.Length + paddingLength)];
        value.CopyTo(result);
        result.AsSpan(value.Length).Fill((byte)paddingLength);
        return result;
    }

    private static byte[] UnpadMinimumKey(ReadOnlySpan<byte> value)
    {
        if (value.Length <= 64)
            throw new CryptographicException(
                "A wrapped cipher blob CEK must contain a padded COSE key.");
        return Unpad(value, requiredBlockSize: null);
    }

    private static byte[] Pad(ReadOnlySpan<byte> value, int minimumLength)
    {
        var paddingLength = Math.Max(minimumLength - value.Length, 1);
        if (paddingLength > byte.MaxValue)
            throw new CryptographicException("Cipher blob padding is too large.");
        var result = new byte[checked(Math.Max(minimumLength, value.Length + 1))];
        value.CopyTo(result);
        result.AsSpan(value.Length).Fill((byte)paddingLength);
        return result;
    }

    private static byte[] Unpad(ReadOnlySpan<byte> value, int? requiredBlockSize)
    {
        if (value.IsEmpty
            || (requiredBlockSize is int blockSize && value.Length % blockSize != 0))
        {
            throw new CryptographicException("Cipher blob padding is invalid.");
        }
        var paddingLength = value[^1];
        if (paddingLength == 0 || paddingLength > value.Length)
            throw new CryptographicException("Cipher blob padding length is invalid.");

        var mismatch = 0;
        foreach (var current in value[^paddingLength..])
            mismatch |= current ^ paddingLength;
        if (mismatch != 0)
            throw new CryptographicException("Cipher blob padding bytes are invalid.");
        return value[..^paddingLength].ToArray();
    }

    private static byte[] DecodeBase64Canonical(string value)
    {
        if (value.Length > MaximumEnvelopeBytes * 2)
            throw new FormatException("Cipher blob DataEnvelope exceeds the size limit.");
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new FormatException("Cipher blob DataEnvelope is not Base64.", exception);
        }
        if (decoded.Length == 0
            || decoded.Length > MaximumEnvelopeBytes
            || Convert.ToBase64String(decoded) != value)
        {
            Zero(decoded);
            throw new FormatException(
                "Cipher blob DataEnvelope is not canonical RFC 4648 Base64.");
        }
        return decoded;
    }

    private static CborReader NewReader(ReadOnlyMemory<byte> encoded)
    {
        if (encoded.IsEmpty || encoded.Length > MaximumEnvelopeBytes)
            throw new FormatException("COSE object is empty or exceeds the size limit.");
        return new CborReader(
            encoded,
            CborConformanceMode.Lax,
            allowMultipleRootLevelValues: false);
    }

    private static void EnsureFinished(CborReader reader)
    {
        if (reader.BytesRemaining != 0 || reader.PeekState() != CborReaderState.Finished)
            throw new FormatException("COSE object contains trailing data.");
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }

    private sealed class CekMaterial(long algorithm, byte[] keyId, byte[] key) : IDisposable
    {
        public long Algorithm { get; } = algorithm;
        public byte[] KeyId { get; } = keyId;
        public byte[] Key { get; } = key;

        public void Dispose()
        {
            Zero(KeyId);
            Zero(Key);
        }
    }

    private sealed class ParsedEnvelope(
        long algorithm,
        byte[] keyId,
        byte[] nonce,
        byte[] protectedHeader,
        byte[] ciphertextAndTag) : IDisposable
    {
        public long Algorithm { get; } = algorithm;
        public byte[] KeyId { get; } = keyId;
        public byte[] Nonce { get; } = nonce;
        public byte[] ProtectedHeader { get; } = protectedHeader;
        public byte[] CiphertextAndTag { get; } = ciphertextAndTag;

        public void Dispose()
        {
            Zero(KeyId);
            Zero(Nonce);
            Zero(ProtectedHeader);
            Zero(CiphertextAndTag);
        }
    }
}
