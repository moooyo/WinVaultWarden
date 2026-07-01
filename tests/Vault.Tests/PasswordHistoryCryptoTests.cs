using Api.Dtos;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class PasswordHistoryCryptoTests
{
    private static SymmetricCryptoKey Key() =>
        new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    [Fact]
    public void Decrypt_Then_Encrypt_RoundTrips_History()
    {
        var crypto = new CryptoService();
        var key = Key();
        var encOld = crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("old-pass-1"), key).ToString();
        var dto = new CipherDto(
            Id: "c1", Type: 1, Name: crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("Item"), key).ToString(),
            Notes: null, Key: null, OrganizationId: null, FolderId: null, Favorite: false, Reprompt: 0,
            Login: null, Card: null, Identity: null, SecureNote: null, SshKey: null, Fields: null,
            CreationDate: null, RevisionDate: null, DeletedDate: null, Attachments: null,
            PasswordHistory: new[] { new PasswordHistoryDto(encOld, "2026-01-02T03:04:05.0000000Z") });

        var decryptor = new VaultDecryptor(crypto);
        var cipher = decryptor.DecryptCipher(dto, key);
        var entry = Assert.Single(cipher.PasswordHistory);
        Assert.Equal("old-pass-1", entry.Password);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05.0000000Z"), entry.LastUsedDate);

        // encrypt 回 request，用同 key 解密验证一致
        var req = new CipherEncryptor(crypto).Encrypt(cipher, key);
        var reqEntry = Assert.Single(req.PasswordHistory!);
        Assert.Equal("old-pass-1",
            crypto.DecryptToString(reqEntry.Password, /* item key */ ResolveKey(crypto, req, key)));
        // DateTimeOffset.ToString("o") 对 UTC 值输出 "+00:00" 偏移量,不是 "Z"（与 DateTime.Kind=Utc 不同）。
        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", reqEntry.LastUsedDate);
    }

    // Encrypt 用随机 item key 包裹进 req.Key；解出它来验证历史密文
    private static SymmetricCryptoKey ResolveKey(CryptoService crypto, CipherRequest req, SymmetricCryptoKey userKey) =>
        crypto.DecryptItemKey(req.Key!, userKey);

    [Fact]
    public void Decrypt_SkipsEntryWithEmptyPassword()
    {
        var crypto = new CryptoService();
        var key = Key();
        var dto = new CipherDto(
            Id: "c1", Type: 1, Name: crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("Item"), key).ToString(),
            Notes: null, Key: null, OrganizationId: null, FolderId: null, Favorite: false, Reprompt: 0,
            Login: null, Card: null, Identity: null, SecureNote: null, SshKey: null, Fields: null,
            CreationDate: null, RevisionDate: null, DeletedDate: null, Attachments: null,
            PasswordHistory: new[] { new PasswordHistoryDto(null, "2026-01-02T03:04:05Z") });
        var cipher = new VaultDecryptor(crypto).DecryptCipher(dto, key);
        Assert.Empty(cipher.PasswordHistory);
    }

    [Fact]
    public void Encrypt_EmptyHistory_SendsNull()
    {
        var crypto = new CryptoService();
        var cipher = new Cipher { Id = "c1", Type = Core.Enums.CipherType.Login, Name = "n" };
        var req = new CipherEncryptor(crypto).Encrypt(cipher, Key());
        Assert.Null(req.PasswordHistory);
    }
}
