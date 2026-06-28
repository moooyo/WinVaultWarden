using System.Security.Cryptography;
using System.Text;
using Api.Dtos;
using Core.Enums;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultDecryptorAttachmentTests
{
    private readonly CryptoService _crypto = new();
    private readonly SymmetricCryptoKey _userKey = new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    private VaultDecryptor NewDecryptor() => new(_crypto);

    private string Enc(string plaintext, SymmetricCryptoKey key)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), key).ToString();

    // 把一个 64 字节附件密钥用 itemKey 包裹成 EncString 字符串(模拟服务端 wire 上的 key 字段)。
    private string WrapAttKey(SymmetricCryptoKey attKey, SymmetricCryptoKey itemKey)
        => _crypto.Encrypt(attKey.FullKey, itemKey).ToString();

    private static SymmetricCryptoKey NewAttKey() => new(RandomNumberGenerator.GetBytes(64));

    private static CipherDto BareLoginDto(CipherAttachmentDto[]? attachments, string? cipherKeyEnc)
        => new(
            Id: "c-att", Type: (int)CipherType.Login, Name: null, Notes: null,
            Key: cipherKeyEnc, OrganizationId: null, FolderId: null, Favorite: false, Reprompt: 0,
            Login: new LoginDto(null, null, null, null), Card: null, Identity: null,
            SecureNote: null, SshKey: null, Fields: null,
            CreationDate: null, RevisionDate: null, DeletedDate: null)
        {
            Attachments = attachments,
        };

    [Fact]
    public void ResolveItemKey_NoCipherKey_ReturnsUserKey()
    {
        var dto = BareLoginDto(attachments: null, cipherKeyEnc: null);

        var resolved = NewDecryptor().ResolveItemKey(dto, _userKey);

        Assert.Same(_userKey, resolved);
    }

    [Fact]
    public void ResolveItemKey_WithCipherKey_DecryptsItemKey()
    {
        var itemKey = NewAttKey();
        var cipherKeyEnc = _crypto.Encrypt(itemKey.FullKey, _userKey).ToString();
        var dto = BareLoginDto(attachments: null, cipherKeyEnc: cipherKeyEnc);

        var resolved = NewDecryptor().ResolveItemKey(dto, _userKey);

        Assert.NotSame(_userKey, resolved);
        Assert.Equal(itemKey.FullKey, resolved.FullKey);
    }

    [Fact]
    public void DecryptAttachments_NewFormat_UnwrapsAttKeyAndFileName()
    {
        var itemKey = _userKey;
        var attKey = NewAttKey();
        var att = new CipherAttachmentDto(
            Id: "att-1",
            FileName: Enc("report.pdf", attKey),
            Key: WrapAttKey(attKey, itemKey),
            Size: "12345",
            SizeName: "12 KB",
            Url: "https://files.example/att-1");

        var result = NewDecryptor().DecryptAttachments(new[] { att }, itemKey);

        var decoded = Assert.Single(result);
        Assert.Equal("att-1", decoded.Id);
        Assert.Equal("report.pdf", decoded.FileName);
        Assert.Equal(12345L, decoded.Size);
        Assert.Equal("12 KB", decoded.SizeName);
    }

    [Fact]
    public void DecryptAttachments_LegacyFormat_NoAttKeyUsesItemKey()
    {
        // 旧格式:att.Key 为空,fileName 直接用 itemKey 加密。
        var itemKey = _userKey;
        var att = new CipherAttachmentDto(
            Id: "att-legacy",
            FileName: Enc("legacy.txt", itemKey),
            Key: null,
            Size: "9",
            SizeName: "9 B",
            Url: null);

        var result = NewDecryptor().DecryptAttachments(new[] { att }, itemKey);

        var decoded = Assert.Single(result);
        Assert.Equal("att-legacy", decoded.Id);
        Assert.Equal("legacy.txt", decoded.FileName);
        Assert.Equal(9L, decoded.Size);
    }

    [Fact]
    public void DecryptAttachments_BadAttachment_IsSkippedGoodRemains()
    {
        var itemKey = _userKey;
        var wrongKey = NewAttKey();
        // 坏附件:用错误的 itemKey 包裹 attKey,unwrap 时 MAC 校验失败。
        var bad = new CipherAttachmentDto(
            Id: "att-bad",
            FileName: Enc("bad.bin", wrongKey),
            Key: _crypto.Encrypt(wrongKey.FullKey, wrongKey).ToString(),
            Size: "1",
            SizeName: "1 B",
            Url: null);
        var goodKey = NewAttKey();
        var good = new CipherAttachmentDto(
            Id: "att-good",
            FileName: Enc("good.bin", goodKey),
            Key: WrapAttKey(goodKey, itemKey),
            Size: "2",
            SizeName: "2 B",
            Url: null);

        var result = NewDecryptor().DecryptAttachments(new[] { bad, good }, itemKey);

        var decoded = Assert.Single(result);
        Assert.Equal("att-good", decoded.Id);
        Assert.Equal("good.bin", decoded.FileName);
    }

    [Fact]
    public void DecryptAttachments_NullArray_ReturnsEmpty()
    {
        var result = NewDecryptor().DecryptAttachments(null, _userKey);

        Assert.Empty(result);
    }

    [Fact]
    public void DecryptAttachments_BadSize_DefaultsToZero()
    {
        var attKey = NewAttKey();
        var att = new CipherAttachmentDto(
            Id: "att-nosize",
            FileName: Enc("x.bin", attKey),
            Key: WrapAttKey(attKey, _userKey),
            Size: null,
            SizeName: null,
            Url: null);

        var result = NewDecryptor().DecryptAttachments(new[] { att }, _userKey);

        var decoded = Assert.Single(result);
        Assert.Equal(0L, decoded.Size);
        Assert.Equal("", decoded.SizeName);
    }

    [Fact]
    public void Decrypt_FullCipher_PopulatesAttachmentsViaDecryptCipher()
    {
        var attKey = NewAttKey();
        var att = new CipherAttachmentDto(
            Id: "att-on-cipher",
            FileName: Enc("doc.txt", attKey),
            Key: WrapAttKey(attKey, _userKey),
            Size: "5",
            SizeName: "5 B",
            Url: "https://files.example/att-on-cipher");
        var dto = new CipherDto(
            Id: "c-1", Type: (int)CipherType.Login, Name: Enc("Item", _userKey), Notes: null,
            Key: null, OrganizationId: null, FolderId: null, Favorite: false, Reprompt: 0,
            Login: new LoginDto(null, null, null, null), Card: null, Identity: null,
            SecureNote: null, SshKey: null, Fields: null,
            CreationDate: null, RevisionDate: null, DeletedDate: null)
        {
            Attachments = new[] { att },
        };
        var sync = new SyncResponse("sync", new ProfileDto("u1", "me@example.com", "Me", null, null), [], [dto]);

        var result = NewDecryptor().Decrypt(sync, _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        var decodedAtt = Assert.Single(cipher.Attachments);
        Assert.Equal("att-on-cipher", decodedAtt.Id);
        Assert.Equal("doc.txt", decodedAtt.FileName);
        Assert.Equal(5L, decodedAtt.Size);
        Assert.Equal("5 B", decodedAtt.SizeName);
    }
}
