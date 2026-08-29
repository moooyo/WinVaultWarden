using System.Security.Cryptography;
using Core.Enums;

namespace Crypto;

/// <summary>
/// Bitwarden EncArrayBuffer framing: <c>type(1) || IV(16) || MAC(32) || ciphertext</c>.
/// Send files and attachments use authenticated AES-256-CBC, encryption type 2.
/// </summary>
public static class EncArrayBuffer
{
    public const byte EncryptionTypeByte = (byte)EncryptionType.AesCbc256_HmacSha256_B64;
    public const int TypeLength = 1;
    public const int IvLength = 16;
    public const int MacLength = 32;
    public const int CipherBlockLength = 16;
    public const int MetadataLength = TypeLength + IvLength + MacLength;
    public const int MinimumCiphertextLength = CipherBlockLength;
    public const int MinimumEncodedLength = MetadataLength + MinimumCiphertextLength;

    public static long GetEncodedLength(long plaintextLength)
    {
        if (plaintextLength < 0)
            throw new ArgumentOutOfRangeException(nameof(plaintextLength));

        var blockCount = checked((plaintextLength / CipherBlockLength) + 1L);
        return checked(MetadataLength + (blockCount * CipherBlockLength));
    }

    public static byte[] Pack(EncString enc)
    {
        if (enc.Type != EncryptionType.AesCbc256_HmacSha256_B64)
            throw new CryptographicException($"EncArrayBuffer only supports encryption type 2, not {(int)enc.Type}.");
        if (enc.Mac is null)
            throw new CryptographicException("EncArrayBuffer requires a MAC.");
        if (enc.Iv.Length != IvLength)
            throw new CryptographicException($"EncArrayBuffer IV must contain {IvLength} bytes.");
        if (enc.Mac.Length != MacLength)
            throw new CryptographicException($"EncArrayBuffer MAC must contain {MacLength} bytes.");
        ValidateCiphertextLength(enc.Ct.LongLength);

        var buffer = new byte[MetadataLength + enc.Ct.Length];
        buffer[0] = EncryptionTypeByte;
        var offset = TypeLength;
        Buffer.BlockCopy(enc.Iv, 0, buffer, offset, enc.Iv.Length);
        offset += enc.Iv.Length;
        Buffer.BlockCopy(enc.Mac, 0, buffer, offset, enc.Mac.Length);
        offset += enc.Mac.Length;
        Buffer.BlockCopy(enc.Ct, 0, buffer, offset, enc.Ct.Length);
        return buffer;
    }

    public static EncString Unpack(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length < MinimumEncodedLength)
            throw new CryptographicException("EncArrayBuffer does not contain a complete ciphertext block.");
        if (buffer[0] != EncryptionTypeByte)
            throw new CryptographicException($"Unexpected EncArrayBuffer encryption type: {buffer[0]}.");

        ValidateCiphertextLength(buffer.LongLength - MetadataLength);

        var iv = buffer[TypeLength..(TypeLength + IvLength)];
        var mac = buffer[(TypeLength + IvLength)..MetadataLength];
        var ct = buffer[MetadataLength..];
        return new EncString(EncryptionType.AesCbc256_HmacSha256_B64, iv, ct, mac);
    }

    public static void ValidateCiphertextLength(long ciphertextLength)
    {
        if (ciphertextLength < MinimumCiphertextLength || ciphertextLength % CipherBlockLength != 0)
        {
            throw new CryptographicException(
                $"EncArrayBuffer ciphertext must contain at least one complete {CipherBlockLength}-byte block.");
        }
    }
}
