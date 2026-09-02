using System.Security.Cryptography;

namespace Crypto.V2;

/// <summary>
/// Implements the C2SP XAES-256-GCM extended-nonce AEAD.
/// </summary>
public static class Xaes256Gcm
{
    public const int KeySize = 32;
    public const int NonceSize = 24;
    public const int TagSize = 16;

    public static byte[] Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> associatedData)
    {
        ValidateInputs(key, nonce);

        var derivedKey = DeriveKey(key, nonce);
        try
        {
            var result = new byte[checked(plaintext.Length + TagSize)];
            using var aesGcm = new AesGcm(derivedKey, TagSize);
            aesGcm.Encrypt(
                nonce[12..],
                plaintext,
                result.AsSpan(0, plaintext.Length),
                result.AsSpan(plaintext.Length, TagSize),
                associatedData);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    public static byte[] Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertextAndTag,
        ReadOnlySpan<byte> associatedData)
    {
        ValidateInputs(key, nonce);
        if (ciphertextAndTag.Length < TagSize)
            throw new CryptographicException("XAES-256-GCM ciphertext is shorter than its authentication tag.");

        var derivedKey = DeriveKey(key, nonce);
        var plaintext = new byte[ciphertextAndTag.Length - TagSize];
        try
        {
            using var aesGcm = new AesGcm(derivedKey, TagSize);
            aesGcm.Decrypt(
                nonce[12..],
                ciphertextAndTag[..^TagSize],
                ciphertextAndTag[^TagSize..],
                plaintext,
                associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    internal static byte[] DeriveKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        ValidateInputs(key, nonce);

        Span<byte> zero = stackalloc byte[16];
        Span<byte> l = stackalloc byte[16];
        Span<byte> k1 = stackalloc byte[16];
        Span<byte> message = stackalloc byte[16];
        var keyCopy = key.ToArray();

        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            aes.Key = keyCopy;

            aes.EncryptEcb(zero, l, PaddingMode.None);
            ShiftLeftOne(l, k1);
            if ((l[0] & 0x80) != 0)
                k1[^1] ^= 0x87;

            var derived = new byte[KeySize];
            try
            {
                BuildKdfMessage(1, nonce, k1, message);
                aes.EncryptEcb(message, derived.AsSpan(0, 16), PaddingMode.None);

                BuildKdfMessage(2, nonce, k1, message);
                aes.EncryptEcb(message, derived.AsSpan(16, 16), PaddingMode.None);
                return derived;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(derived);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
            CryptographicOperations.ZeroMemory(zero);
            CryptographicOperations.ZeroMemory(l);
            CryptographicOperations.ZeroMemory(k1);
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private static void BuildKdfMessage(
        byte counter,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> cmacSubkey,
        Span<byte> destination)
    {
        destination.Clear();
        destination[1] = counter;
        destination[2] = 0x58;
        nonce[..12].CopyTo(destination[4..]);
        for (var i = 0; i < destination.Length; i++)
            destination[i] ^= cmacSubkey[i];
    }

    private static void ShiftLeftOne(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        byte carry = 0;
        for (var i = source.Length - 1; i >= 0; i--)
        {
            var nextCarry = (byte)(source[i] >> 7);
            destination[i] = (byte)((source[i] << 1) | carry);
            carry = nextCarry;
        }
    }

    private static void ValidateInputs(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"XAES-256-GCM keys must be exactly {KeySize} bytes.", nameof(key));
        if (nonce.Length != NonceSize)
            throw new ArgumentException($"XAES-256-GCM nonces must be exactly {NonceSize} bytes.", nameof(nonce));
    }
}
