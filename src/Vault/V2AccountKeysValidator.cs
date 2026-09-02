using System.Formats.Cbor;
using System.Security.Cryptography;
using Api.Dtos;
using Crypto.V2;

namespace Vault;

public sealed class V2AccountKeysValidator
{
    public const int CurrentSecurityVersion = 2;
    private const int RsaKeySizeBits = 2048;
    private const int MaximumBase64Length = 1024 * 1024;
    private readonly Func<V2UserKey, string, CoseContentFormat, byte[]> _decrypt;

    public V2AccountKeysValidator()
        : this(static (key, value, contentFormat) => key.Decrypt(value, contentFormat))
    {
    }

    internal V2AccountKeysValidator(
        Func<V2UserKey, string, CoseContentFormat, byte[]> decrypt)
    {
        _decrypt = decrypt ?? throw new ArgumentNullException(nameof(decrypt));
    }

    public ValidatedV2AccountKeys Validate(
        AccountKeysResponse accountKeys,
        V2UserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(accountKeys);
        ArgumentNullException.ThrowIfNull(userKey);

        try
        {
            var encryptionPair = accountKeys.PublicKeyEncryptionKeyPair
                ?? throw Invalid("The public-key encryption pair is missing.");
            var signaturePair = accountKeys.SignatureKeyPair
                ?? throw Invalid("The signature key pair is missing.");
            var securityState = accountKeys.SecurityState
                ?? throw Invalid("The signed security state is missing.");

            RequireValue(encryptionPair.WrappedPrivateKey, "wrapped private key");
            RequireValue(signaturePair.WrappedSigningKey, "wrapped signing key");
            RequireValue(securityState.SecurityState, "signed security state");
            RequireOptionalValue(encryptionPair.PublicKey, "account public key");
            RequireOptionalValue(encryptionPair.SignedPublicKey, "signed public key");
            RequireOptionalValue(signaturePair.VerifyingKey, "account verifying key");
            if (securityState.SecurityVersion != CurrentSecurityVersion)
                throw Invalid("The account security version is unsupported.");

            var privateKeyBytes = _decrypt(
                userKey,
                encryptionPair.WrappedPrivateKey!,
                CoseContentFormat.Pkcs8PrivateKey);
            byte[]? signingKeyBytes = null;
            byte[]? derivedPublicKey = null;
            try
            {
                signingKeyBytes = _decrypt(
                    userKey,
                    signaturePair.WrappedSigningKey!,
                    CoseContentFormat.CoseKey);
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(privateKeyBytes, out var bytesRead);
                if (bytesRead != privateKeyBytes.Length || rsa.KeySize != RsaKeySizeBits)
                    throw Invalid("The account private key is invalid.");
                derivedPublicKey = rsa.ExportSubjectPublicKeyInfo();
                if (encryptionPair.PublicKey is not null)
                {
                    RequireCanonicalBase64Equals(
                        encryptionPair.PublicKey,
                        derivedPublicKey,
                        "account public key");
                }

                using var signingKey = AccountSigningKey.ImportSigningKeyCose(signingKeyBytes);
                var derivedVerifyingKey = signingKey.ExportVerifyingKeyCoseBase64();
                if (signaturePair.VerifyingKey is not null
                    && !FixedTimeTextEquals(
                        derivedVerifyingKey,
                        signaturePair.VerifyingKey))
                {
                    throw Invalid("The account verifying key does not match its signing key.");
                }

                if (encryptionPair.SignedPublicKey is not null)
                {
                    var signedPublicKeyPayload = CoseSign1Codec.VerifyAndExtract(
                        encryptionPair.SignedPublicKey,
                        derivedVerifyingKey,
                        BitwardenSigningNamespace.SignedPublicKey);
                    try
                    {
                        ValidateSignedPublicKeyPayload(
                            signedPublicKeyPayload,
                            derivedPublicKey);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(signedPublicKeyPayload);
                    }
                }

                var securityStatePayload = CoseSign1Codec.VerifyAndExtract(
                    securityState.SecurityState!,
                    derivedVerifyingKey,
                    BitwardenSigningNamespace.SecurityState);
                try
                {
                    ValidateSecurityStatePayload(
                        securityStatePayload,
                        securityState.SecurityVersion);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(securityStatePayload);
                }

                return new ValidatedV2AccountKeys(
                    encryptionPair.WrappedPrivateKey!,
                    userKey.KeyIdHex,
                    securityState.SecurityVersion);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
                if (signingKeyBytes is not null)
                    CryptographicOperations.ZeroMemory(signingKeyBytes);
                if (derivedPublicKey is not null)
                    CryptographicOperations.ZeroMemory(derivedPublicKey);
            }
        }
        catch (Exception ex) when (ex is
            CborContentException
                or CryptographicException
                or FormatException
                or ArgumentException
                or OverflowException)
        {
            throw new InvalidDataException(
                "The V2 account cryptographic state could not be authenticated.",
                ex);
        }
    }

    private static void ValidateSignedPublicKeyPayload(
        ReadOnlyMemory<byte> payload,
        ReadOnlySpan<byte> expectedPublicKey)
    {
        var reader = NewReader(payload);
        if (reader.ReadStartMap() != 3)
            throw Invalid("The signed public-key payload has an invalid shape.");

        int? algorithm = null;
        int? contentFormat = null;
        byte[]? publicKey = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < 3; index++)
            {
                var name = reader.ReadTextString();
                if (!seen.Add(name))
                    throw Invalid("The signed public-key payload has a duplicate field.");
                switch (name)
                {
                    case "algorithm":
                        algorithm = checked((int)reader.ReadInt64());
                        break;
                    case "contentFormat":
                        contentFormat = checked((int)reader.ReadInt64());
                        break;
                    case "publicKey":
                        publicKey = reader.ReadByteString();
                        break;
                    default:
                        throw Invalid("The signed public-key payload contains an unknown field.");
                }
            }

            reader.ReadEndMap();
            EnsureFinished(reader);
            if (algorithm != 0
                || contentFormat != 0
                || publicKey is null
                || !CryptographicOperations.FixedTimeEquals(publicKey, expectedPublicKey))
            {
                throw Invalid("The signed public key does not match the account private key.");
            }
        }
        finally
        {
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static void ValidateSecurityStatePayload(
        ReadOnlyMemory<byte> payload,
        int expectedVersion)
    {
        var reader = NewReader(payload);
        var mapLength = reader.ReadStartMap();
        if (mapLength is not (1 or 2))
            throw Invalid("The signed security state has an invalid shape.");

        ulong? version = null;
        byte[]? entityId = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < mapLength.Value; index++)
            {
                var name = reader.ReadTextString();
                if (!seen.Add(name))
                    throw Invalid("The signed security state has a duplicate field.");
                switch (name)
                {
                    case "version":
                        version = reader.ReadUInt64();
                        break;
                    case "entityId":
                        entityId = reader.ReadByteString();
                        break;
                    default:
                        throw Invalid("The signed security state contains an unknown field.");
                }
            }

            reader.ReadEndMap();
            EnsureFinished(reader);
            if (version != (ulong)expectedVersion
                || (mapLength == 2 && entityId is not { Length: 16 })
                || (mapLength == 1 && entityId is not null))
            {
                throw Invalid("The signed security state is invalid.");
            }
        }
        finally
        {
            if (entityId is not null)
                CryptographicOperations.ZeroMemory(entityId);
        }
    }

    private static CborReader NewReader(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty || payload.Length > MaximumBase64Length)
            throw Invalid("The signed account payload exceeds the protocol limit.");
        return new CborReader(
            payload,
            CborConformanceMode.Lax,
            allowMultipleRootLevelValues: false);
    }

    private static void EnsureFinished(CborReader reader)
    {
        if (reader.BytesRemaining != 0 || reader.PeekState() != CborReaderState.Finished)
            throw Invalid("The signed account payload contains trailing data.");
    }

    private static void RequireCanonicalBase64Equals(
        string encoded,
        ReadOnlySpan<byte> expected,
        string name)
    {
        if (encoded.Length > MaximumBase64Length)
            throw Invalid($"The {name} exceeds the protocol limit.");
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            throw Invalid($"The {name} is not valid Base64.", ex);
        }

        try
        {
            if (!string.Equals(Convert.ToBase64String(decoded), encoded, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(decoded, expected))
            {
                throw Invalid($"The {name} does not match the derived key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static bool FixedTimeTextEquals(string left, string right)
    {
        if (left.Length != right.Length)
            return false;
        var mismatch = 0;
        for (var index = 0; index < left.Length; index++)
            mismatch |= left[index] ^ right[index];
        return mismatch == 0;
    }

    private static void RequireValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumBase64Length)
            throw Invalid($"The {name} is missing or exceeds the protocol limit.");
    }

    private static void RequireOptionalValue(string? value, string name)
    {
        if (value is not null)
            RequireValue(value, name);
    }

    private static InvalidDataException Invalid(string message, Exception? inner = null) =>
        new(message, inner);
}

public sealed record ValidatedV2AccountKeys(
    string WrappedPrivateKey,
    string UserKeyId,
    int SecurityVersion);
