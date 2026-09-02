using System.Security.Cryptography;

namespace Crypto.V2;

/// <summary>
/// Wire-ready companion material for registering a new V2 account for trusted-device encryption.
/// </summary>
public sealed class V2TdeRegistrationMaterial : IDisposable
{
    private int _disposed;

    private V2TdeRegistrationMaterial(
        V2AccountKeyMaterial accountKeys,
        string resetPasswordKey,
        V2TrustedDeviceKeyMaterial? trustedDeviceKeys)
    {
        AccountKeys = accountKeys;
        ResetPasswordKey = resetPasswordKey;
        TrustedDeviceKeys = trustedDeviceKeys;
    }

    public V2AccountKeyMaterial AccountKeys { get; }
    public string ResetPasswordKey { get; }
    public V2TrustedDeviceKeyMaterial? TrustedDeviceKeys { get; }

    public static V2TdeRegistrationMaterial Create(ReadOnlySpan<byte> organizationPublicKeySpki)
        => Create(organizationPublicKeySpki, trustDevice: true);

    public static V2TdeRegistrationMaterial Create(
        ReadOnlySpan<byte> organizationPublicKeySpki,
        bool trustDevice)
    {
        var accountKeys = V2AccountKeyMaterial.Generate();
        try
        {
            var resetPasswordKey = V2TdeKeyWrapping.WrapUserKey(
                accountKeys.UserKey,
                organizationPublicKeySpki);
            var trustedDeviceKeys = trustDevice
                ? V2TrustedDeviceKeyMaterial.Create(accountKeys.UserKey)
                : null;
            return new V2TdeRegistrationMaterial(accountKeys, resetPasswordKey, trustedDeviceKeys);
        }
        catch
        {
            accountKeys.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        TrustedDeviceKeys?.Dispose();
        AccountKeys.Dispose();
    }
}

/// <summary>
/// Owns a legacy 64-byte DeviceKey and the three encrypted values stored on a trusted device row.
/// </summary>
public sealed class V2TrustedDeviceKeyMaterial : IDisposable
{
    private byte[] _deviceKey;
    private int _disposed;

    private V2TrustedDeviceKeyMaterial(
        byte[] deviceKey,
        string encryptedUserKey,
        string encryptedPublicKey,
        string encryptedPrivateKey)
    {
        _deviceKey = deviceKey;
        EncryptedUserKey = encryptedUserKey;
        EncryptedPublicKey = encryptedPublicKey;
        EncryptedPrivateKey = encryptedPrivateKey;
    }

    public string EncryptedUserKey { get; }
    public string EncryptedPublicKey { get; }
    public string EncryptedPrivateKey { get; }

    public byte[] ExportDeviceKey()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _deviceKey.ToArray();
    }

    internal string ExportDeviceKeyBase64()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Convert.ToBase64String(_deviceKey);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CryptographicOperations.ZeroMemory(_deviceKey);
        _deviceKey = Array.Empty<byte>();
    }

    public static V2TrustedDeviceKeyMaterial Create(V2UserKey userKey)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        var deviceKey = RandomNumberGenerator.GetBytes(64);
        try
        {
            using var deviceRsa = RSA.Create(2048);
            if (deviceRsa.KeySize != 2048)
                throw new CryptographicException("Trusted devices must use RSA-2048.");

            var publicKey = deviceRsa.ExportSubjectPublicKeyInfo();
            var privateKey = deviceRsa.ExportPkcs8PrivateKey();
            try
            {
                return new V2TrustedDeviceKeyMaterial(
                    deviceKey,
                    V2TdeKeyWrapping.WrapUserKey(userKey, publicKey),
                    userKey.Encrypt(publicKey, CoseContentFormat.SpkiPublicKey),
                    V2TdeKeyWrapping.EncryptLegacyType2(privateKey, deviceKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
                CryptographicOperations.ZeroMemory(privateKey);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(deviceKey);
            throw;
        }
    }
}

internal static class V2TdeKeyWrapping
{
    internal static string WrapUserKey(V2UserKey userKey, ReadOnlySpan<byte> publicKeySpki)
    {
        ArgumentNullException.ThrowIfNull(userKey);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeySpki, out var bytesRead);
        if (bytesRead != publicKeySpki.Length || rsa.KeySize != 2048)
            throw new CryptographicException("TDE wrapping keys must be complete RSA-2048 SPKI values.");

        var transport = userKey.ExportTransport();
        byte[]? ciphertext = null;
        try
        {
            ciphertext = rsa.Encrypt(transport, RSAEncryptionPadding.OaepSHA1);
            return $"4.{Convert.ToBase64String(ciphertext)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    internal static string EncryptLegacyType2(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> deviceKey)
    {
        if (deviceKey.Length != 64)
            throw new ArgumentException("Legacy DeviceKey values must be exactly 64 bytes.", nameof(deviceKey));

        var iv = RandomNumberGenerator.GetBytes(16);
        byte[]? ciphertext = null;
        byte[]? macInput = null;
        byte[]? mac = null;
        var encryptionKey = deviceKey[..32].ToArray();
        var macKey = deviceKey[32..].ToArray();
        try
        {
            using (var aes = Aes.Create())
            {
                aes.Key = encryptionKey;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);
            }

            macInput = new byte[checked(iv.Length + ciphertext.Length)];
            iv.CopyTo(macInput, 0);
            ciphertext.CopyTo(macInput, iv.Length);
            using var hmac = new HMACSHA256(macKey);
            mac = hmac.ComputeHash(macInput);

            return $"2.{Convert.ToBase64String(iv)}|{Convert.ToBase64String(ciphertext)}|{Convert.ToBase64String(mac)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(macKey);
            if (ciphertext is not null)
                CryptographicOperations.ZeroMemory(ciphertext);
            if (macInput is not null)
                CryptographicOperations.ZeroMemory(macInput);
            if (mac is not null)
                CryptographicOperations.ZeroMemory(mac);
        }
    }
}
