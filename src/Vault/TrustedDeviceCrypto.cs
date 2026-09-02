using System.Security.Cryptography;
using Core.Enums;
using Crypto;
using Crypto.V2;

namespace Vault;

/// <summary>
/// Implements the legacy Bitwarden 64-byte trusted-device key graph. New
/// material uses type 2/type 4, while reads also accept historical type 3/5/6.
/// </summary>
public sealed class TrustedDeviceCrypto
{
    public const int DeviceKeyLength = 64;
    public const int UserKeyLength = 64;
    public const int RsaKeySizeBits = 2048;

    private const int RsaCiphertextLength = RsaKeySizeBits / 8;
    private const int AesBlockLength = 16;
    private const int AesIvLength = 16;
    private const int HmacLength = 32;
    private const string FailureMessage = "Trusted device cryptographic material is invalid.";

    private readonly CryptoService _crypto;

    public TrustedDeviceCrypto(CryptoService crypto)
    {
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
    }

    /// <summary>
    /// Generates a device key and RSA key pair, then produces the three protected values required
    /// by the Bitwarden trusted-device API.
    /// </summary>
    public TrustedDeviceTrustMaterial CreateTrustMaterial(SymmetricCryptoKey userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        ValidateAuthenticatedKey(userKey);

        SymmetricCryptoKey? deviceKey = null;
        byte[]? devicePublicKey = null;
        byte[]? devicePrivateKey = null;

        try
        {
            deviceKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(DeviceKeyLength));

            using var rsa = RSA.Create(RsaKeySizeBits);
            if (rsa.KeySize != RsaKeySizeBits)
                throw CreateSafeFailure();

            devicePublicKey = rsa.ExportSubjectPublicKeyInfo();
            devicePrivateKey = rsa.ExportPkcs8PrivateKey();

            var encryptedUserKey = _crypto.EncryptRsa(userKey.FullKey, devicePublicKey);
            var encryptedPublicKey = _crypto.Encrypt(devicePublicKey, userKey);
            var encryptedPrivateKey = _crypto.Encrypt(devicePrivateKey, deviceKey);

            ValidateRsaEnvelope(encryptedUserKey);
            ValidateAesEnvelope(encryptedPublicKey);
            ValidateAesEnvelope(encryptedPrivateKey);

            var result = new TrustedDeviceTrustMaterial(
                deviceKey,
                encryptedUserKey,
                encryptedPublicKey,
                encryptedPrivateKey);
            deviceKey = null;
            return result;
        }
        catch (Exception ex) when (IsExpectedCryptographicFailure(ex))
        {
            throw CreateSafeFailure();
        }
        finally
        {
            deviceKey?.Dispose();
            Zero(devicePublicKey);
            Zero(devicePrivateKey);
        }
    }

    /// <summary>
    /// Recovers the user key by unwrapping the device private key and then decapsulating the
    /// RSA-protected user key.
    /// </summary>
    public SymmetricCryptoKey DecryptUserKey(
        SymmetricCryptoKey deviceKey,
        EncString encryptedPrivateKey,
        EncString encryptedUserKey)
    {
        byte[]? userKeyBytes = null;

        try
        {
            userKeyBytes = DecryptUserKeyTransport(
                deviceKey,
                encryptedPrivateKey,
                encryptedUserKey);
            if (userKeyBytes.Length != UserKeyLength)
                throw CreateSafeFailure();

            var userKey = new SymmetricCryptoKey(userKeyBytes);
            userKeyBytes = null;
            return userKey;
        }
        catch (Exception ex) when (IsExpectedCryptographicFailure(ex))
        {
            throw CreateSafeFailure();
        }
        finally
        {
            Zero(userKeyBytes);
        }
    }

    public byte[] DecryptUserKeyTransport(
        SymmetricCryptoKey deviceKey,
        EncString encryptedPrivateKey,
        EncString encryptedUserKey)
    {
        byte[]? devicePrivateKey = null;
        byte[]? userKeyBytes = null;
        try
        {
            if (deviceKey is null || encryptedPrivateKey is null || encryptedUserKey is null)
                throw CreateSafeFailure();

            ValidateAuthenticatedKey(deviceKey);
            ValidateAesEnvelope(encryptedPrivateKey);
            ValidateRsaEnvelope(encryptedUserKey);

            devicePrivateKey = _crypto.Decrypt(encryptedPrivateKey, deviceKey);
            ValidateDevicePrivateKey(devicePrivateKey);
            userKeyBytes = _crypto.DecryptRsa(encryptedUserKey, devicePrivateKey);
            if (userKeyBytes.Length == UserKeyLength)
            {
                var result = userKeyBytes;
                userKeyBytes = null;
                return result;
            }

            using (V2UserKey.ImportTransport(userKeyBytes))
            {
            }
            var v2Result = userKeyBytes;
            userKeyBytes = null;
            return v2Result;
        }
        catch (Exception ex) when (IsExpectedCryptographicFailure(ex))
        {
            throw CreateSafeFailure();
        }
        finally
        {
            Zero(devicePrivateKey);
            Zero(userKeyBytes);
        }
    }

    private static void ValidateAuthenticatedKey(SymmetricCryptoKey key)
    {
        if (key.IsDisposed
            || key.FullKey.Length != DeviceKeyLength
            || key.EncKey.Length != 32
            || key.MacKey is null
            || key.MacKey.Length != 32
            || !CryptographicOperations.FixedTimeEquals(
                key.FullKey.AsSpan(0, 32),
                key.EncKey)
            || !CryptographicOperations.FixedTimeEquals(
                key.FullKey.AsSpan(32, 32),
                key.MacKey))
        {
            throw CreateSafeFailure();
        }
    }

    private static void ValidateAesEnvelope(EncString encrypted)
    {
        if (encrypted.Type != EncryptionType.AesCbc256_HmacSha256_B64
            || encrypted.Iv is null
            || encrypted.Iv.Length != AesIvLength
            || encrypted.Mac is null
            || encrypted.Mac.Length != HmacLength
            || encrypted.Ct is null
            || encrypted.Ct.Length < AesBlockLength
            || encrypted.Ct.Length % AesBlockLength != 0)
        {
            throw CreateSafeFailure();
        }
    }

    private static void ValidateRsaEnvelope(EncString encrypted)
    {
        var expectedMac = encrypted.Type.HasLegacyRsaMac();
        if (!encrypted.Type.IsRsaEnvelope()
            || encrypted.Iv is null
            || encrypted.Iv.Length != 0
            || (expectedMac && encrypted.Mac is not { Length: EncString.LegacyRsaMacLength })
            || (!expectedMac && encrypted.Mac is not null)
            || encrypted.Ct is null
            || encrypted.Ct.Length != RsaCiphertextLength)
        {
            throw CreateSafeFailure();
        }
    }

    private static void ValidateDevicePrivateKey(byte[] privateKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
        if (bytesRead != privateKey.Length || rsa.KeySize != RsaKeySizeBits)
            throw CreateSafeFailure();
    }

    private static bool IsExpectedCryptographicFailure(Exception exception) =>
        exception is CryptographicException or ArgumentException or FormatException;

    private static CryptographicException CreateSafeFailure() => new(FailureMessage);

    private static void Zero(byte[]? value)
    {
        if (value is not null)
            CryptographicOperations.ZeroMemory(value);
    }
}

/// <summary>
/// Owns the local device key and the protected values uploaded for a trusted device.
/// </summary>
public sealed class TrustedDeviceTrustMaterial : IDisposable
{
    private SymmetricCryptoKey? _deviceKey;

    internal TrustedDeviceTrustMaterial(
        SymmetricCryptoKey deviceKey,
        EncString encryptedUserKey,
        EncString encryptedPublicKey,
        EncString encryptedPrivateKey)
    {
        _deviceKey = deviceKey ?? throw new ArgumentNullException(nameof(deviceKey));
        EncryptedUserKey = encryptedUserKey ?? throw new ArgumentNullException(nameof(encryptedUserKey));
        EncryptedPublicKey = encryptedPublicKey ?? throw new ArgumentNullException(nameof(encryptedPublicKey));
        EncryptedPrivateKey = encryptedPrivateKey ?? throw new ArgumentNullException(nameof(encryptedPrivateKey));
    }

    /// <summary>
    /// Gets the locally retained device key. The material object owns this key.
    /// </summary>
    public SymmetricCryptoKey DeviceKey =>
        Volatile.Read(ref _deviceKey) ?? throw new ObjectDisposedException(nameof(TrustedDeviceTrustMaterial));

    public EncString EncryptedUserKey { get; }
    public EncString EncryptedPublicKey { get; }
    public EncString EncryptedPrivateKey { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _deviceKey, null)?.Dispose();
    }
}
