using System.Security.Cryptography;
using System.Text;

namespace Crypto;

// Bitwarden 附件加密链。与 bitwarden/clients 实现对齐。
// 每个附件有独立 64B 附件密钥 attKey(enc32+mac32):
//   - fileName 与文件体用 attKey 加密;文件体为 EncArrayBuffer 二进制。
//   - attKey 本身用 itemKey 包裹后作为附件 DTO 的 key 字段(EncString type 2)。
// 旧格式附件(key 为空)由调用方改传 itemKey 直接加解密,本类不感知该分支。
public sealed class AttachmentCryptoService
{
    private const int AttachmentKeyLength = 64;

    private readonly CryptoService _crypto;

    public AttachmentCryptoService(CryptoService crypto) => _crypto = crypto;

    // 64 字节随机附件密钥(前 32 enc、后 32 mac)。
    public SymmetricCryptoKey GenerateAttachmentKey()
        => new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(AttachmentKeyLength));

    // key 字段 = Encrypt(attKey.FullKey, itemKey) 的 EncString 字符串(type 2)。
    public string WrapKey(SymmetricCryptoKey attachmentKey, SymmetricCryptoKey itemKey)
        => _crypto.Encrypt(attachmentKey.FullKey, itemKey).ToString();

    // 解开 key 字段还原 attKey。
    public SymmetricCryptoKey UnwrapKey(string wrapped, SymmetricCryptoKey itemKey)
        => new SymmetricCryptoKey(_crypto.Decrypt(EncString.Parse(wrapped), itemKey));

    // fileName = Encrypt(utf8(fileName), key) 的 EncString 字符串。
    public string EncryptFileName(string fileName, SymmetricCryptoKey key)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(fileName), key).ToString();

    public string? DecryptFileName(string? enc, SymmetricCryptoKey key)
    {
        if (string.IsNullOrEmpty(enc))
            return null;
        return Encoding.UTF8.GetString(_crypto.Decrypt(EncString.Parse(enc), key));
    }

    // 文件体 = EncArrayBuffer.Pack(Encrypt(plaintext, key)) 二进制。
    public byte[] EncryptFile(byte[] plaintext, SymmetricCryptoKey key)
        => EncArrayBuffer.Pack(_crypto.Encrypt(plaintext, key));

    // 解析 EncArrayBuffer 文件体并解密为明文字节。
    public byte[] DecryptFile(byte[] buffer, SymmetricCryptoKey key)
        => _crypto.Decrypt(EncArrayBuffer.Unpack(buffer), key);
}
