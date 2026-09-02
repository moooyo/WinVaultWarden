using System.Formats.Cbor;
using System.Security.Cryptography;

namespace Crypto.V2;

/// <summary>
/// Implements Bitwarden's untagged, namespaced COSE_Sign1 account objects.
/// </summary>
public static class CoseSign1Codec
{
    public const long Mldsa44Algorithm = CosePrimitives.Mldsa44Algorithm;
    public const long EdDsaAlgorithm = CosePrimitives.EdDsaAlgorithm;
    public const long SigningNamespaceLabel = CosePrimitives.SigningNamespaceLabel;

    public static string SignToBase64(
        ReadOnlySpan<byte> cborPayload,
        AccountSigningKey signingKey,
        BitwardenSigningNamespace signingNamespace)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        var protectedHeader = CosePrimitives.EncodeSign1ProtectedHeader(
            signingKey.CoseAlgorithm,
            signingKey.KeyIdBytes,
            signingNamespace);
        var toBeSigned = CosePrimitives.EncodeSign1ToBeSigned(protectedHeader, cborPayload);
        byte[]? signature = null;
        byte[]? encoded = null;

        try
        {
            signature = signingKey.Sign(toBeSigned);
            encoded = CosePrimitives.EncodeCoseSign1(protectedHeader, cborPayload, signature);
            return Convert.ToBase64String(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedHeader);
            CryptographicOperations.ZeroMemory(toBeSigned);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
            if (encoded is not null)
                CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public static byte[] VerifyAndExtract(
        string coseSign1Base64,
        string verifyingKeyCoseBase64,
        BitwardenSigningNamespace expectedNamespace)
    {
        var signedObject = CosePrimitives.DecodeBase64Exact(coseSign1Base64, nameof(coseSign1Base64));
        var verifyingKeyBytes = CosePrimitives.DecodeBase64Exact(
            verifyingKeyCoseBase64,
            nameof(verifyingKeyCoseBase64));

        try
        {
            using var verifyingKey = AccountVerifyingKey.Import(verifyingKeyBytes);
            var parsed = ParseSignedObject(signedObject);
            try
            {
                if (parsed.SigningNamespace != expectedNamespace)
                    throw new CryptographicException("COSE_Sign1 namespace does not match the expected object type.");
                if (parsed.Algorithm != verifyingKey.CoseAlgorithm)
                    throw new CryptographicException("COSE_Sign1 algorithm does not match its verifying key.");
                if (!CryptographicOperations.FixedTimeEquals(parsed.KeyId, verifyingKey.KeyId))
                    throw new CryptographicException("COSE_Sign1 key identifier does not match its verifying key.");

                var toBeSigned = CosePrimitives.EncodeSign1ToBeSigned(parsed.ProtectedHeader, parsed.Payload);
                try
                {
                    if (!verifyingKey.Verify(toBeSigned, parsed.Signature))
                        throw new CryptographicException("COSE_Sign1 signature verification failed.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(toBeSigned);
                }

                return parsed.Payload.ToArray();
            }
            finally
            {
                parsed.Dispose();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signedObject);
            CryptographicOperations.ZeroMemory(verifyingKeyBytes);
        }
    }

    private static ParsedCoseSign1 ParseSignedObject(ReadOnlyMemory<byte> encoded)
    {
        var reader = CosePrimitives.NewCanonicalReader(encoded);
        if (reader.ReadStartArray() != 4)
            throw new FormatException("COSE_Sign1 must be a definite four-element array.");

        var protectedHeader = reader.ReadByteString();
        if (protectedHeader.Length == 0)
            throw new FormatException("COSE_Sign1 protected header is empty.");
        if (reader.ReadStartMap() != 0)
            throw new FormatException("Bitwarden COSE_Sign1 unprotected header must be empty.");
        reader.ReadEndMap();
        var payload = reader.ReadByteString();
        var signature = reader.ReadByteString();
        reader.ReadEndArray();
        CosePrimitives.EnsureFinished(reader);

        byte[]? keyId = null;
        try
        {
            var metadata = ParseProtectedHeader(protectedHeader, out keyId);
            var expectedSignatureLength = metadata.Algorithm switch
            {
                CosePrimitives.EdDsaAlgorithm => 64,
                CosePrimitives.Mldsa44Algorithm => MLDsaAlgorithm.MLDsa44.SignatureSizeInBytes,
                _ => throw new FormatException("Unsupported COSE_Sign1 algorithm."),
            };
            if (signature.Length != expectedSignatureLength)
                throw new FormatException("COSE_Sign1 signature length is invalid for its algorithm.");

            var result = new ParsedCoseSign1(
                metadata.Algorithm,
                metadata.SigningNamespace,
                keyId,
                protectedHeader,
                payload,
                signature);
            keyId = null;
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(protectedHeader);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(signature);
            if (keyId is not null)
                CryptographicOperations.ZeroMemory(keyId);
            throw;
        }
    }

    private static (long Algorithm, BitwardenSigningNamespace SigningNamespace) ParseProtectedHeader(
        ReadOnlyMemory<byte> encoded,
        out byte[] keyId)
    {
        var reader = CosePrimitives.NewCanonicalReader(encoded);
        if (reader.ReadStartMap() != 4)
            throw new FormatException("COSE_Sign1 protected header must contain exactly four parameters.");

        long? algorithm = null;
        int? contentType = null;
        byte[]? parsedKeyId = null;
        BitwardenSigningNamespace? signingNamespace = null;
        var seen = new HashSet<long>();

        try
        {
            for (var index = 0; index < 4; index++)
            {
                var label = reader.ReadInt64();
                if (!seen.Add(label))
                    throw new FormatException("COSE_Sign1 protected header contains a duplicate parameter.");

                switch (label)
                {
                    case 1:
                        algorithm = reader.ReadInt64();
                        break;
                    case 3:
                        contentType = checked((int)reader.ReadInt64());
                        break;
                    case 4:
                        parsedKeyId = reader.ReadByteString();
                        break;
                    case CosePrimitives.SigningNamespaceLabel:
                        var namespaceValue = checked((int)reader.ReadInt64());
                        if (!Enum.IsDefined(typeof(BitwardenSigningNamespace), namespaceValue))
                            throw new FormatException("COSE_Sign1 signing namespace is unsupported.");
                        signingNamespace = (BitwardenSigningNamespace)namespaceValue;
                        break;
                    default:
                        throw new FormatException($"Unsupported COSE_Sign1 protected parameter: {label}.");
                }
            }

            reader.ReadEndMap();
            CosePrimitives.EnsureFinished(reader);
            if (algorithm is not (CosePrimitives.EdDsaAlgorithm or CosePrimitives.Mldsa44Algorithm)
                || contentType != CosePrimitives.CborContentFormat
                || parsedKeyId is not { Length: V2UserKey.KeyIdSize }
                || signingNamespace is null)
            {
                throw new FormatException("COSE_Sign1 protected header is invalid for a Bitwarden account object.");
            }

            var canonical = CosePrimitives.EncodeSign1ProtectedHeader(
                algorithm.Value,
                parsedKeyId,
                signingNamespace.Value);
            try
            {
                if (!encoded.Span.SequenceEqual(canonical))
                    throw new FormatException("COSE_Sign1 protected header is not canonically encoded.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }

            keyId = parsedKeyId;
            parsedKeyId = null;
            return (algorithm.Value, signingNamespace.Value);
        }
        finally
        {
            if (parsedKeyId is not null)
                CryptographicOperations.ZeroMemory(parsedKeyId);
        }
    }

    private sealed class ParsedCoseSign1 : IDisposable
    {
        private readonly byte[] _keyId;
        private readonly byte[] _protectedHeader;
        private readonly byte[] _payload;
        private readonly byte[] _signature;

        internal ParsedCoseSign1(
            long algorithm,
            BitwardenSigningNamespace signingNamespace,
            byte[] keyId,
            byte[] protectedHeader,
            byte[] payload,
            byte[] signature)
        {
            Algorithm = algorithm;
            SigningNamespace = signingNamespace;
            _keyId = keyId;
            _protectedHeader = protectedHeader;
            _payload = payload;
            _signature = signature;
        }

        internal long Algorithm { get; }
        internal BitwardenSigningNamespace SigningNamespace { get; }
        internal ReadOnlySpan<byte> KeyId => _keyId;
        internal ReadOnlySpan<byte> ProtectedHeader => _protectedHeader;
        internal ReadOnlySpan<byte> Payload => _payload;
        internal ReadOnlySpan<byte> Signature => _signature;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_keyId);
            CryptographicOperations.ZeroMemory(_protectedHeader);
            CryptographicOperations.ZeroMemory(_payload);
            CryptographicOperations.ZeroMemory(_signature);
        }
    }
}
