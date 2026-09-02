using System.Formats.Cbor;
using System.Security.Cryptography;

namespace Crypto.V2;

/// <summary>
/// Owns a newly generated Account Encryption V2 user key and the wire-ready encrypted account state.
/// </summary>
public sealed class V2AccountKeyMaterial : IDisposable
{
    private int _disposed;

    private V2AccountKeyMaterial(
        V2UserKey userKey,
        string accountPublicKey,
        string wrappedPrivateKey,
        string signedPublicKey,
        string wrappedSigningKey,
        string verifyingKey,
        string securityState)
    {
        UserKey = userKey;
        AccountPublicKey = accountPublicKey;
        WrappedPrivateKey = wrappedPrivateKey;
        SignedPublicKey = signedPublicKey;
        WrappedSigningKey = wrappedSigningKey;
        VerifyingKey = verifyingKey;
        SecurityState = securityState;
    }

    public V2UserKey UserKey { get; }
    public string AccountPublicKey { get; }
    public string WrappedPrivateKey { get; }
    public string SignedPublicKey { get; }
    public string SignatureAlgorithm => "mldsa44";
    public string WrappedSigningKey { get; }
    public string VerifyingKey { get; }
    public string SecurityState { get; }
    public int SecurityVersion => 2;

    public static V2AccountKeyMaterial Generate()
        => GenerateCore(exportObserver: null);

    internal static V2AccountKeyMaterial GenerateWithExportObserverForTest(
        Action<int, byte[]> exportObserver)
    {
        ArgumentNullException.ThrowIfNull(exportObserver);
        return GenerateCore(exportObserver);
    }

    private static V2AccountKeyMaterial GenerateCore(Action<int, byte[]>? exportObserver)
    {
        var userKey = V2UserKey.Generate();
        try
        {
            using var rsa = RSA.Create(2048);
            if (rsa.KeySize != 2048)
                throw new CryptographicException("Account encryption keys must use RSA-2048.");
            using var signingKey = AccountSigningKey.GenerateMldsa44();

            byte[]? privateKey = null;
            byte[]? publicKey = null;
            byte[]? signingKeyCose = null;
            byte[]? signedPublicKeyPayload = null;
            byte[]? securityStatePayload = null;

            try
            {
                privateKey = rsa.ExportPkcs8PrivateKey();
                exportObserver?.Invoke(1, privateKey);
                publicKey = rsa.ExportSubjectPublicKeyInfo();
                exportObserver?.Invoke(2, publicKey);
                signingKeyCose = signingKey.ExportSigningKeyCose();
                exportObserver?.Invoke(3, signingKeyCose);

                var wrappedPrivateKey = userKey.Encrypt(privateKey, CoseContentFormat.Pkcs8PrivateKey);
                var wrappedSigningKey = userKey.Encrypt(signingKeyCose, CoseContentFormat.CoseKey);
                signedPublicKeyPayload = EncodeSignedPublicKeyPayload(publicKey);
                securityStatePayload = EncodeSecurityStatePayload();

                return new V2AccountKeyMaterial(
                    userKey,
                    Convert.ToBase64String(publicKey),
                    wrappedPrivateKey,
                    CoseSign1Codec.SignToBase64(
                        signedPublicKeyPayload,
                        signingKey,
                        BitwardenSigningNamespace.SignedPublicKey),
                    wrappedSigningKey,
                    signingKey.ExportVerifyingKeyCoseBase64(),
                    CoseSign1Codec.SignToBase64(
                        securityStatePayload,
                        signingKey,
                        BitwardenSigningNamespace.SecurityState));
            }
            finally
            {
                if (privateKey is not null)
                    CryptographicOperations.ZeroMemory(privateKey);
                if (publicKey is not null)
                    CryptographicOperations.ZeroMemory(publicKey);
                if (signingKeyCose is not null)
                    CryptographicOperations.ZeroMemory(signingKeyCose);
                if (signedPublicKeyPayload is not null)
                    CryptographicOperations.ZeroMemory(signedPublicKeyPayload);
                if (securityStatePayload is not null)
                    CryptographicOperations.ZeroMemory(securityStatePayload);
            }
        }
        catch
        {
            userKey.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        UserKey.Dispose();
    }

    internal static byte[] EncodeSignedPublicKeyPayload(ReadOnlySpan<byte> publicKeySpki)
    {
        // Field order intentionally matches serde's struct order in the Bitwarden Rust SDK.
        var writer = new CborWriter(CborConformanceMode.Lax, convertIndefiniteLengthEncodings: true);
        writer.WriteStartMap(3);
        writer.WriteTextString("algorithm");
        writer.WriteInt32(0);
        writer.WriteTextString("contentFormat");
        writer.WriteInt32(0);
        writer.WriteTextString("publicKey");
        writer.WriteByteString(publicKeySpki);
        writer.WriteEndMap();
        return writer.Encode();
    }

    internal static byte[] EncodeSecurityStatePayload()
    {
        var writer = CosePrimitives.NewCanonicalWriter();
        writer.WriteStartMap(1);
        writer.WriteTextString("version");
        writer.WriteInt32(2);
        writer.WriteEndMap();
        return writer.Encode();
    }
}

public sealed record V2AccountCryptographicState(
    string WrappedPrivateKey,
    string? SignedPublicKey,
    string WrappedSigningKey,
    string? VerifyingKey,
    string SecurityState,
    int SecurityVersion);

/// <summary>
/// Owns decrypted V2 account private keys after all wrapped-state and signature checks succeed.
/// </summary>
public sealed class V2UnlockedAccountKeys : IDisposable
{
    private byte[] _privateKeyPkcs8;
    private int _disposed;

    internal V2UnlockedAccountKeys(byte[] privateKeyPkcs8, AccountSigningKey signingKey)
    {
        _privateKeyPkcs8 = privateKeyPkcs8;
        SigningKey = signingKey;
    }

    public AccountSigningKey SigningKey { get; }

    public byte[] ExportPrivateKeyPkcs8()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _privateKeyPkcs8.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        SigningKey.Dispose();
        CryptographicOperations.ZeroMemory(_privateKeyPkcs8);
        _privateKeyPkcs8 = Array.Empty<byte>();
    }
}

public static class V2AccountCryptographicStateCodec
{
    public static V2UnlockedAccountKeys UnlockAndVerify(
        V2UserKey userKey,
        V2AccountCryptographicState state)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        ArgumentNullException.ThrowIfNull(state);
        if (state.SecurityVersion != 2)
            throw new CryptographicException("The Account Encryption V2 security version is unsupported.");

        byte[]? privateKeyPkcs8 = null;
        byte[]? signingKeyCose = null;
        AccountSigningKey? signingKey = null;
        byte[]? derivedVerifyingKey = null;
        byte[]? suppliedVerifyingKey = null;

        try
        {
            privateKeyPkcs8 = userKey.Decrypt(
                state.WrappedPrivateKey,
                CoseContentFormat.Pkcs8PrivateKey);
            signingKeyCose = userKey.Decrypt(
                state.WrappedSigningKey,
                CoseContentFormat.CoseKey);

            using (var rsa = RSA.Create())
            {
                rsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
                if (bytesRead != privateKeyPkcs8.Length || rsa.KeySize != 2048)
                    throw new CryptographicException("Wrapped account private key is not a complete RSA-2048 PKCS#8 value.");
            }

            signingKey = AccountSigningKey.ImportSigningKeyCose(signingKeyCose);
            derivedVerifyingKey = signingKey.ExportVerifyingKeyCose();
            if (state.VerifyingKey is not null)
            {
                suppliedVerifyingKey = CosePrimitives.DecodeBase64Exact(
                    state.VerifyingKey,
                    nameof(state.VerifyingKey));
                if (!derivedVerifyingKey.AsSpan().SequenceEqual(suppliedVerifyingKey))
                    throw new CryptographicException(
                        "Wrapped signing key does not match the supplied verifying key.");
            }
            var derivedVerifyingKeyBase64 = Convert.ToBase64String(derivedVerifyingKey);

            if (state.SignedPublicKey is not null)
            {
                var payload = CoseSign1Codec.VerifyAndExtract(
                    state.SignedPublicKey,
                    derivedVerifyingKeyBase64,
                    BitwardenSigningNamespace.SignedPublicKey);
                try
                {
                    var signedSpki = ParseSignedPublicKeyPayload(payload);
                    try
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out _);
                        var derivedSpki = rsa.ExportSubjectPublicKeyInfo();
                        try
                        {
                            if (!derivedSpki.AsSpan().SequenceEqual(signedSpki))
                                throw new CryptographicException("Signed account public key does not match the wrapped private key.");
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(derivedSpki);
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(signedSpki);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }

            var securityPayload = CoseSign1Codec.VerifyAndExtract(
                state.SecurityState,
                derivedVerifyingKeyBase64,
                BitwardenSigningNamespace.SecurityState);
            try
            {
                var signedVersion = ParseSecurityVersion(securityPayload);
                if (signedVersion != state.SecurityVersion)
                    throw new CryptographicException("Signed security version does not match the response metadata.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(securityPayload);
            }

            var result = new V2UnlockedAccountKeys(privateKeyPkcs8, signingKey);
            privateKeyPkcs8 = null!;
            signingKey = null;
            return result;
        }
        finally
        {
            if (privateKeyPkcs8 is not null)
                CryptographicOperations.ZeroMemory(privateKeyPkcs8);
            if (signingKeyCose is not null)
                CryptographicOperations.ZeroMemory(signingKeyCose);
            if (derivedVerifyingKey is not null)
                CryptographicOperations.ZeroMemory(derivedVerifyingKey);
            if (suppliedVerifyingKey is not null)
                CryptographicOperations.ZeroMemory(suppliedVerifyingKey);
            signingKey?.Dispose();
        }
    }

    private static byte[] ParseSignedPublicKeyPayload(ReadOnlyMemory<byte> payload)
    {
        var reader = CosePrimitives.NewStrictReader(payload);
        if (reader.ReadStartMap() != 3)
            throw new FormatException("Signed public key payload must contain exactly three fields.");

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
                    throw new FormatException("Signed public key payload contains a duplicate field.");
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
                        throw new FormatException($"Unsupported signed public key field: {name}.");
                }
            }
            reader.ReadEndMap();
            CosePrimitives.EnsureFinished(reader);
            if (algorithm != 0 || contentFormat != 0 || publicKey is null)
                throw new FormatException("Signed public key payload is not RSA-OAEP-SHA1 SPKI.");

            var result = publicKey;
            publicKey = null;
            return result;
        }
        finally
        {
            if (publicKey is not null)
                CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static int ParseSecurityVersion(ReadOnlyMemory<byte> payload)
    {
        var reader = CosePrimitives.NewStrictReader(payload);
        var fieldCount = reader.ReadStartMap();
        if (fieldCount is not (1 or 2))
            throw new FormatException("Security state payload is invalid.");

        int? version = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < fieldCount.Value; index++)
        {
            var name = reader.ReadTextString();
            if (!seen.Add(name))
                throw new FormatException("Security state payload contains a duplicate field.");
            switch (name)
            {
                case "version":
                    version = checked((int)reader.ReadUInt64());
                    break;
                case "entityId":
                    // Historical V2 security states carried the account UUID. The current SDK
                    // ignores it while retaining wire compatibility.
                    if (reader.ReadByteString().Length != 16)
                        throw new FormatException("Security state entity identifier must be 16 bytes.");
                    break;
                default:
                    throw new FormatException($"Unsupported security state field: {name}.");
            }
        }
        reader.ReadEndMap();
        CosePrimitives.EnsureFinished(reader);
        if (version is null or < 2)
            throw new CryptographicException("Signed security state is older than Account Encryption V2.");
        return version.Value;
    }
}
