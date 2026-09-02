using System.Formats.Cbor;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

namespace Crypto.V2;

public enum AccountSignatureAlgorithm
{
    Ed25519,
    Mldsa44,
}

/// <summary>
/// Owns a Bitwarden account signing key. New keys are always ML-DSA-44; Ed25519 is read-only
/// compatibility for existing Account Encryption V2 accounts.
/// </summary>
public sealed class AccountSigningKey : IDisposable
{
    private const int OkpKeyType = 1;
    private const int AkpKeyType = 7;
    private const int SignOperation = 1;
    private const int VerifyOperation = 2;
    private const int Ed25519Curve = 6;

    private readonly byte[] _keyId;
    private readonly byte[] _seed;
    private readonly byte[] _publicKey;
    private readonly MLDsa? _mldsa;
    private int _disposed;

    private AccountSigningKey(
        AccountSignatureAlgorithm algorithm,
        byte[] keyId,
        byte[] seed,
        byte[] publicKey,
        MLDsa? mldsa)
    {
        Algorithm = algorithm;
        _keyId = keyId;
        _seed = seed;
        _publicKey = publicKey;
        _mldsa = mldsa;
    }

    public AccountSignatureAlgorithm Algorithm { get; }
    public static bool IsMldsa44Supported => true;
    public static bool IsNativeMldsa44Supported => MLDsa.IsSupported;

    public string KeyIdHex
    {
        get
        {
            ThrowIfDisposed();
            return CosePrimitives.ToLowerHex(_keyId);
        }
    }

    public static AccountSigningKey GenerateMldsa44()
        => MLDsa.IsSupported ? GenerateNativeMldsa44() : GenerateManagedMldsa44();

    internal static AccountSigningKey GenerateManagedMldsa44()
    {
        byte[]? seed = RandomNumberGenerator.GetBytes(ManagedMldsa44.SeedSize);
        byte[]? publicKey = null;
        try
        {
            publicKey = ManagedMldsa44.DerivePublicKey(seed);
            var result = new AccountSigningKey(
                AccountSignatureAlgorithm.Mldsa44,
                RandomNumberGenerator.GetBytes(V2UserKey.KeyIdSize),
                seed,
                publicKey,
                null);
            seed = null;
            publicKey = null;
            return result;
        }
        finally
        {
            if (seed is not null)
                CryptographicOperations.ZeroMemory(seed);
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static AccountSigningKey GenerateNativeMldsa44()
    {
        var mldsa = MLDsa.GenerateKey(MLDsaAlgorithm.MLDsa44);
        byte[]? seed = null;
        byte[]? publicKey = null;
        try
        {
            seed = mldsa.ExportMLDsaPrivateSeed();
            publicKey = mldsa.ExportMLDsaPublicKey();
            var result = new AccountSigningKey(
                AccountSignatureAlgorithm.Mldsa44,
                RandomNumberGenerator.GetBytes(V2UserKey.KeyIdSize),
                seed,
                publicKey,
                mldsa);
            seed = null;
            publicKey = null;
            mldsa = null!;
            return result;
        }
        finally
        {
            if (seed is not null)
                CryptographicOperations.ZeroMemory(seed);
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
            mldsa?.Dispose();
        }
    }

    public static AccountSigningKey ImportSigningKeyCoseBase64(string value)
    {
        var decoded = CosePrimitives.DecodeBase64Exact(value, nameof(value));
        try
        {
            return ImportSigningKeyCose(decoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public static AccountSigningKey ImportSigningKeyCose(ReadOnlySpan<byte> encoded)
        => ImportSigningKeyCoseCore(encoded, preferNativeMldsa: true);

    internal static AccountSigningKey ImportSigningKeyCoseManaged(ReadOnlySpan<byte> encoded)
        => ImportSigningKeyCoseCore(encoded, preferNativeMldsa: false);

    private static AccountSigningKey ImportSigningKeyCoseCore(
        ReadOnlySpan<byte> encoded,
        bool preferNativeMldsa)
    {
        if (encoded.IsEmpty || encoded.Length > CosePrimitives.MaximumEncodedObjectLength)
            throw new FormatException("Account signing COSE key exceeds the protocol size limit.");
        var parsed = SigningCoseKeyCodec.Parse(encoded, requirePrivateKey: true);
        try
        {
            return parsed.Algorithm switch
            {
                AccountSignatureAlgorithm.Ed25519 => ImportEd25519(parsed),
                AccountSignatureAlgorithm.Mldsa44 => ImportMldsa44(parsed, preferNativeMldsa),
                _ => throw new CryptographicException("Unsupported account signature algorithm."),
            };
        }
        finally
        {
            parsed.Dispose();
        }
    }

    public byte[] ExportSigningKeyCose()
    {
        ThrowIfDisposed();
        return Algorithm switch
        {
            AccountSignatureAlgorithm.Ed25519 => EncodeEd25519SigningKey(),
            AccountSignatureAlgorithm.Mldsa44 => EncodeMldsa44SigningKey(),
            _ => throw new CryptographicException("Unsupported account signature algorithm."),
        };
    }

    internal string ExportSigningKeyCoseBase64()
    {
        var encoded = ExportSigningKeyCose();
        try
        {
            return Convert.ToBase64String(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public byte[] ExportVerifyingKeyCose()
    {
        ThrowIfDisposed();
        return Algorithm switch
        {
            AccountSignatureAlgorithm.Ed25519 => EncodeEd25519VerifyingKey(),
            AccountSignatureAlgorithm.Mldsa44 => EncodeMldsa44VerifyingKey(),
            _ => throw new CryptographicException("Unsupported account signature algorithm."),
        };
    }

    public string ExportVerifyingKeyCoseBase64()
    {
        var encoded = ExportVerifyingKeyCose();
        try
        {
            return Convert.ToBase64String(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _mldsa?.Dispose();
        CryptographicOperations.ZeroMemory(_keyId);
        CryptographicOperations.ZeroMemory(_seed);
        CryptographicOperations.ZeroMemory(_publicKey);
    }

    internal ReadOnlySpan<byte> KeyIdBytes
    {
        get
        {
            ThrowIfDisposed();
            return _keyId;
        }
    }

    internal long CoseAlgorithm => Algorithm switch
    {
        AccountSignatureAlgorithm.Ed25519 => CosePrimitives.EdDsaAlgorithm,
        AccountSignatureAlgorithm.Mldsa44 => CosePrimitives.Mldsa44Algorithm,
        _ => throw new CryptographicException("Unsupported account signature algorithm."),
    };

    internal byte[] Sign(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (Algorithm == AccountSignatureAlgorithm.Mldsa44)
        {
            if (_mldsa is null)
                return ManagedMldsa44.Sign(_seed, data);

            var input = data.ToArray();
            try
            {
                return _mldsa.SignData(input, Array.Empty<byte>());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(input);
            }
        }

        var signature = new byte[64];
        Ed25519.Sign(_seed, data, signature);
        return signature;
    }

    private static AccountSigningKey ImportEd25519(ParsedSigningCoseKey parsed)
    {
        var seed = parsed.TakePrivateSeed();
        var publicKey = new byte[32];
        Ed25519.GeneratePublicKey(seed, publicKey);
        if (parsed.PublicKey is { } encodedPublicKey
            && !CryptographicOperations.FixedTimeEquals(publicKey, encodedPublicKey))
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(publicKey);
            throw new CryptographicException("Ed25519 signing key does not match its encoded public key.");
        }

        return new AccountSigningKey(
            AccountSignatureAlgorithm.Ed25519,
            parsed.TakeKeyId(),
            seed,
            publicKey,
            null);
    }

    private static AccountSigningKey ImportMldsa44(
        ParsedSigningCoseKey parsed,
        bool preferNativeMldsa)
    {
        if (!preferNativeMldsa || !MLDsa.IsSupported)
            return ImportManagedMldsa44(parsed);

        var seed = parsed.TakePrivateSeed();
        MLDsa? mldsa = null;
        byte[]? publicKey = null;
        try
        {
            mldsa = MLDsa.ImportMLDsaPrivateSeed(MLDsaAlgorithm.MLDsa44, seed);
            publicKey = mldsa.ExportMLDsaPublicKey();
            if (parsed.PublicKey is null
                || !CryptographicOperations.FixedTimeEquals(publicKey, parsed.PublicKey))
            {
                throw new CryptographicException("ML-DSA-44 signing key does not match its encoded public key.");
            }

            var result = new AccountSigningKey(
                AccountSignatureAlgorithm.Mldsa44,
                parsed.TakeKeyId(),
                seed,
                publicKey,
                mldsa);
            seed = null!;
            publicKey = null;
            mldsa = null;
            return result;
        }
        finally
        {
            if (seed is not null)
                CryptographicOperations.ZeroMemory(seed);
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
            mldsa?.Dispose();
        }
    }

    private static AccountSigningKey ImportManagedMldsa44(ParsedSigningCoseKey parsed)
    {
        var seed = parsed.TakePrivateSeed();
        byte[]? publicKey = null;
        try
        {
            publicKey = ManagedMldsa44.DerivePublicKey(seed);
            if (parsed.PublicKey is null
                || !CryptographicOperations.FixedTimeEquals(publicKey, parsed.PublicKey))
            {
                throw new CryptographicException(
                    "ML-DSA-44 signing key does not match its encoded public key.");
            }

            var result = new AccountSigningKey(
                AccountSignatureAlgorithm.Mldsa44,
                parsed.TakeKeyId(),
                seed,
                publicKey,
                null);
            seed = null!;
            publicKey = null;
            return result;
        }
        finally
        {
            if (seed is not null)
                CryptographicOperations.ZeroMemory(seed);
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private byte[] EncodeEd25519SigningKey()
    {
        var writer = CosePrimitives.NewCanonicalWriter();
        writer.WriteStartMap(6);
        WriteCommonKeyParameters(writer, OkpKeyType, CosePrimitives.EdDsaAlgorithm, SignOperation);
        writer.WriteInt32(-1);
        writer.WriteInt32(Ed25519Curve);
        writer.WriteInt32(-4);
        writer.WriteByteString(_seed);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private byte[] EncodeEd25519VerifyingKey()
    {
        var writer = CosePrimitives.NewCanonicalWriter();
        writer.WriteStartMap(6);
        WriteCommonKeyParameters(writer, OkpKeyType, CosePrimitives.EdDsaAlgorithm, VerifyOperation);
        writer.WriteInt32(-1);
        writer.WriteInt32(Ed25519Curve);
        writer.WriteInt32(-2);
        writer.WriteByteString(_publicKey);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private byte[] EncodeMldsa44SigningKey()
    {
        var writer = CosePrimitives.NewCanonicalWriter();
        writer.WriteStartMap(6);
        WriteCommonKeyParameters(writer, AkpKeyType, CosePrimitives.Mldsa44Algorithm, SignOperation);
        writer.WriteInt32(-1);
        writer.WriteByteString(_publicKey);
        writer.WriteInt32(-2);
        writer.WriteByteString(_seed);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private byte[] EncodeMldsa44VerifyingKey()
    {
        var writer = CosePrimitives.NewCanonicalWriter();
        writer.WriteStartMap(5);
        WriteCommonKeyParameters(writer, AkpKeyType, CosePrimitives.Mldsa44Algorithm, VerifyOperation);
        writer.WriteInt32(-1);
        writer.WriteByteString(_publicKey);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private void WriteCommonKeyParameters(CborWriter writer, int keyType, long algorithm, int operation)
    {
        writer.WriteInt32(1);
        writer.WriteInt32(keyType);
        writer.WriteInt32(2);
        writer.WriteByteString(_keyId);
        writer.WriteInt32(3);
        writer.WriteInt64(algorithm);
        writer.WriteInt32(4);
        writer.WriteStartArray(1);
        writer.WriteInt32(operation);
        writer.WriteEndArray();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

internal sealed class AccountVerifyingKey : IDisposable
{
    private readonly byte[] _keyId;
    private readonly byte[] _publicKey;
    private readonly MLDsa? _mldsa;
    private int _disposed;

    private AccountVerifyingKey(
        AccountSignatureAlgorithm algorithm,
        byte[] keyId,
        byte[] publicKey,
        MLDsa? mldsa)
    {
        Algorithm = algorithm;
        _keyId = keyId;
        _publicKey = publicKey;
        _mldsa = mldsa;
    }

    internal AccountSignatureAlgorithm Algorithm { get; }
    internal ReadOnlySpan<byte> KeyId => _keyId;
    internal long CoseAlgorithm => Algorithm == AccountSignatureAlgorithm.Ed25519
        ? CosePrimitives.EdDsaAlgorithm
        : CosePrimitives.Mldsa44Algorithm;

    internal static AccountVerifyingKey Import(ReadOnlySpan<byte> encoded)
        => ImportCore(encoded, preferNativeMldsa: true);

    internal static AccountVerifyingKey ImportManaged(ReadOnlySpan<byte> encoded)
        => ImportCore(encoded, preferNativeMldsa: false);

    private static AccountVerifyingKey ImportCore(
        ReadOnlySpan<byte> encoded,
        bool preferNativeMldsa)
    {
        var parsed = SigningCoseKeyCodec.Parse(encoded, requirePrivateKey: false);
        try
        {
            var publicKey = parsed.TakePublicKey();
            if (parsed.Algorithm == AccountSignatureAlgorithm.Ed25519)
            {
                return new AccountVerifyingKey(
                    parsed.Algorithm,
                    parsed.TakeKeyId(),
                    publicKey,
                    null);
            }

            if (!preferNativeMldsa || !MLDsa.IsSupported)
            {
                return new AccountVerifyingKey(
                    parsed.Algorithm,
                    parsed.TakeKeyId(),
                    publicKey,
                    null);
            }

            MLDsa? mldsa = null;
            try
            {
                mldsa = MLDsa.ImportMLDsaPublicKey(MLDsaAlgorithm.MLDsa44, publicKey);
                var result = new AccountVerifyingKey(
                    parsed.Algorithm,
                    parsed.TakeKeyId(),
                    publicKey,
                    mldsa);
                publicKey = null!;
                mldsa = null;
                return result;
            }
            finally
            {
                if (publicKey is not null)
                    CryptographicOperations.ZeroMemory(publicKey);
                mldsa?.Dispose();
            }
        }
        finally
        {
            parsed.Dispose();
        }
    }

    internal bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Algorithm == AccountSignatureAlgorithm.Mldsa44)
        {
            return _mldsa is not null
                ? _mldsa.VerifyData(data, signature, ReadOnlySpan<byte>.Empty)
                : ManagedMldsa44.Verify(_publicKey, data, signature);
        }

        return Ed25519.Verify(signature, _publicKey, data);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _mldsa?.Dispose();
        CryptographicOperations.ZeroMemory(_keyId);
        CryptographicOperations.ZeroMemory(_publicKey);
    }
}

internal static class ManagedMldsa44
{
    internal const int SeedSize = 32;
    internal const int PublicKeySize = 1312;
    internal const int SignatureSize = 2420;

    private static readonly MLDsaParameters Parameters = MLDsaParameters.ml_dsa_44;

    internal static byte[] DerivePublicKey(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
            throw new CryptographicException("ML-DSA-44 seeds must be exactly 32 bytes.");

        var seedCopy = seed.ToArray();
        try
        {
            var privateKey = MLDsaPrivateKeyParameters.FromSeed(Parameters, seedCopy);
            var publicKey = privateKey.GetPublicKeyEncoded();
            if (publicKey.Length != PublicKeySize)
            {
                CryptographicOperations.ZeroMemory(publicKey);
                throw new CryptographicException("The managed ML-DSA-44 backend returned an invalid public key.");
            }
            return publicKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedCopy);
        }
    }

    internal static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> data)
    {
        if (seed.Length != SeedSize)
            throw new CryptographicException("ML-DSA-44 seeds must be exactly 32 bytes.");

        var seedCopy = seed.ToArray();
        try
        {
            var privateKey = MLDsaPrivateKeyParameters.FromSeed(Parameters, seedCopy);
            var signer = new MLDsaSigner(Parameters, deterministic: false);
            signer.Init(
                forSigning: true,
                new ParametersWithRandom(privateKey, new SecureRandom()));
            signer.BlockUpdate(data);
            var signature = signer.GenerateSignature();
            if (signature.Length != SignatureSize)
            {
                CryptographicOperations.ZeroMemory(signature);
                throw new CryptographicException("The managed ML-DSA-44 backend returned an invalid signature.");
            }
            return signature;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seedCopy);
        }
    }

    internal static bool Verify(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
            return false;

        var publicKeyCopy = publicKey.ToArray();
        var signatureCopy = signature.ToArray();
        try
        {
            var verifyingKey = MLDsaPublicKeyParameters.FromEncoding(Parameters, publicKeyCopy);
            var verifier = new MLDsaSigner(Parameters, deterministic: false);
            verifier.Init(forSigning: false, verifyingKey);
            verifier.BlockUpdate(data);
            return verifier.VerifySignature(signatureCopy);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKeyCopy);
            CryptographicOperations.ZeroMemory(signatureCopy);
        }
    }
}

internal sealed class ParsedSigningCoseKey : IDisposable
{
    private byte[]? _keyId;
    private byte[]? _privateSeed;
    private byte[]? _publicKey;

    internal ParsedSigningCoseKey(
        AccountSignatureAlgorithm algorithm,
        byte[] keyId,
        byte[]? privateSeed,
        byte[]? publicKey)
    {
        Algorithm = algorithm;
        _keyId = keyId;
        _privateSeed = privateSeed;
        _publicKey = publicKey;
    }

    internal AccountSignatureAlgorithm Algorithm { get; }
    internal byte[]? PublicKey => _publicKey;

    internal byte[] TakeKeyId() => Take(ref _keyId, "key identifier");
    internal byte[] TakePrivateSeed() => Take(ref _privateSeed, "private seed");
    internal byte[] TakePublicKey() => Take(ref _publicKey, "public key");

    public void Dispose()
    {
        Clear(ref _keyId);
        Clear(ref _privateSeed);
        Clear(ref _publicKey);
    }

    private static byte[] Take(ref byte[]? value, string name)
    {
        var result = value ?? throw new CryptographicException($"COSE key is missing its {name}.");
        value = null;
        return result;
    }

    private static void Clear(ref byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
        value = null;
    }
}

internal static class SigningCoseKeyCodec
{
    internal static ParsedSigningCoseKey Parse(ReadOnlySpan<byte> encoded, bool requirePrivateKey)
    {
        var copy = encoded.ToArray();
        try
        {
            // Historical Bitwarden Ed25519 signing keys place the private-key parameter before
            // the curve parameter. Accept that non-canonical map order while still enforcing the
            // exact field set, definite lengths, unique labels, and no trailing CBOR values.
            var reader = CosePrimitives.NewStrictReader(copy);
            var mapLength = reader.ReadStartMap();
            if (mapLength is null or < 5 or > 6)
                throw new FormatException("Account signing COSE key has an invalid parameter count.");

            int? keyType = null;
            long? algorithm = null;
            byte[]? keyId = null;
            int? operation = null;
            long? curve = null;
            byte[]? minusOneBytes = null;
            byte[]? minusTwoBytes = null;
            byte[]? minusFourBytes = null;
            var seen = new HashSet<long>();

            try
            {
                for (var index = 0; index < mapLength.Value; index++)
                {
                    var label = reader.ReadInt64();
                    if (!seen.Add(label))
                        throw new FormatException("Account signing COSE key contains a duplicate parameter.");

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
                            operation = ReadSingleOperation(reader);
                            break;
                        case -1 when reader.PeekState() is CborReaderState.UnsignedInteger:
                            curve = checked((long)reader.ReadUInt64());
                            break;
                        case -1:
                            minusOneBytes = reader.ReadByteString();
                            break;
                        case -2:
                            minusTwoBytes = reader.ReadByteString();
                            break;
                        case -4:
                            minusFourBytes = reader.ReadByteString();
                            break;
                        default:
                            throw new FormatException($"Unsupported account signing COSE key parameter: {label}.");
                    }
                }

                reader.ReadEndMap();
                CosePrimitives.EnsureFinished(reader);
                if (keyId is not { Length: V2UserKey.KeyIdSize })
                    throw new FormatException("Account signing COSE key identifier must be 16 bytes.");

                if (keyType == 1 && algorithm == CosePrimitives.EdDsaAlgorithm)
                {
                    if (mapLength != 6
                        || curve != 6
                        || operation != (requirePrivateKey ? 1 : 2)
                        || minusOneBytes is not null
                        || (requirePrivateKey && minusFourBytes is not { Length: 32 })
                        || (requirePrivateKey && minusTwoBytes is not null)
                        || (!requirePrivateKey && minusTwoBytes is not { Length: 32 })
                        || (!requirePrivateKey && minusFourBytes is not null))
                    {
                        throw new FormatException("COSE key is not a valid Ed25519 account signing key.");
                    }

                    var result = new ParsedSigningCoseKey(
                        AccountSignatureAlgorithm.Ed25519,
                        keyId,
                        requirePrivateKey ? minusFourBytes : null,
                        minusTwoBytes);
                    keyId = null;
                    minusTwoBytes = null;
                    minusFourBytes = null;
                    return result;
                }

                if (keyType == 7 && algorithm == CosePrimitives.Mldsa44Algorithm)
                {
                    if (mapLength != (requirePrivateKey ? 6 : 5)
                        || operation != (requirePrivateKey ? 1 : 2)
                        || curve is not null
                        || minusOneBytes is not { Length: 1312 }
                        || (requirePrivateKey && minusTwoBytes is not { Length: 32 })
                        || (!requirePrivateKey && minusTwoBytes is not null)
                        || minusFourBytes is not null)
                    {
                        throw new FormatException("COSE key is not a valid ML-DSA-44 account signing key.");
                    }

                    var result = new ParsedSigningCoseKey(
                        AccountSignatureAlgorithm.Mldsa44,
                        keyId,
                        requirePrivateKey ? minusTwoBytes : null,
                        minusOneBytes);
                    keyId = null;
                    minusOneBytes = null;
                    minusTwoBytes = null;
                    return result;
                }

                throw new FormatException("Unsupported account signing COSE key type or algorithm.");
            }
            finally
            {
                Clear(keyId);
                Clear(minusOneBytes);
                Clear(minusTwoBytes);
                Clear(minusFourBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static int ReadSingleOperation(CborReader reader)
    {
        if (reader.ReadStartArray() != 1)
            throw new FormatException("Account signing COSE key must declare exactly one operation.");
        var operation = checked((int)reader.ReadInt64());
        reader.ReadEndArray();
        return operation;
    }

    private static void Clear(byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }
}
