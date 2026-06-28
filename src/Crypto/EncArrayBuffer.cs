using System.Security.Cryptography;
using Core.Enums;

namespace Crypto;

// Bitwarden EncArrayBuffer 二进制封装:concat[ (byte)2, iv(16), mac(32), ct ]。
// 仅支持带 MAC 的 AesCbc256_HmacSha256_B64(type 2)。Send 与附件文件体共用此格式。
public static class EncArrayBuffer
{
    private const int HeaderLength = 1;
    private const int IvLength = 16;
    private const int MacLength = 32;
    private const int MinLength = HeaderLength + IvLength + MacLength; // 49

    // 打包:要求 enc.Mac != null 且 type=AesCbc256_HmacSha256_B64,输出 [(byte)2, iv(16), mac(32), ct]。
    public static byte[] Pack(EncString enc)
    {
        if (enc.Type != EncryptionType.AesCbc256_HmacSha256_B64)
            throw new CryptographicException($"EncArrayBuffer 仅支持 encType 2,实际 {(int)enc.Type}");
        if (enc.Mac is null)
            throw new CryptographicException("EncArrayBuffer 需要带 MAC 的密钥");
        if (enc.Iv.Length != IvLength)
            throw new CryptographicException($"EncArrayBuffer iv 长度须为 {IvLength},实际 {enc.Iv.Length}");
        if (enc.Mac.Length != MacLength)
            throw new CryptographicException($"EncArrayBuffer mac 长度须为 {MacLength},实际 {enc.Mac.Length}");

        var buffer = new byte[HeaderLength + IvLength + MacLength + enc.Ct.Length];
        buffer[0] = (byte)EncryptionType.AesCbc256_HmacSha256_B64; // 2
        var offset = HeaderLength;
        Buffer.BlockCopy(enc.Iv, 0, buffer, offset, enc.Iv.Length);
        offset += enc.Iv.Length;
        Buffer.BlockCopy(enc.Mac, 0, buffer, offset, enc.Mac.Length);
        offset += enc.Mac.Length;
        Buffer.BlockCopy(enc.Ct, 0, buffer, offset, enc.Ct.Length);
        return buffer;
    }

    // 解包:校验 length>=49 且 buffer[0]==2;iv=[1..17],mac=[17..49],ct=[49..]。
    public static EncString Unpack(byte[] buffer)
    {
        if (buffer.Length < MinLength)
            throw new CryptographicException("EncArrayBuffer 长度不足");
        if (buffer[0] != (byte)EncryptionType.AesCbc256_HmacSha256_B64)
            throw new CryptographicException($"非预期 EncArrayBuffer encType: {buffer[0]}");

        var iv = buffer[1..17];
        var mac = buffer[17..49];
        var ct = buffer[49..];
        return new EncString(EncryptionType.AesCbc256_HmacSha256_B64, iv, ct, mac);
    }
}
