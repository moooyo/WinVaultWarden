using System.Security.Cryptography;
using System.Text;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultDecryptorTests
{
    private readonly CryptoService _crypto = new();
    private readonly SymmetricCryptoKey _userKey = new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    private string Enc(string plaintext, SymmetricCryptoKey key)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), key).ToString();

    private static SyncResponse Sync(CipherDto[]? ciphers = null, FolderDto[]? folders = null, ProfileDto? profile = null)
        => new("sync", profile ?? new ProfileDto("u1", "me@example.com", "Me", null, null), folders ?? [], ciphers ?? []);

    private VaultDecryptor NewDecryptor() => new(_crypto);

    private CipherDto LoginDto() => new(
        Id: "c-login", Type: (int)CipherType.Login, Name: Enc("GitHub", _userKey), Notes: Enc("note", _userKey),
        Key: null, OrganizationId: null, FolderId: "f1", Favorite: true, Reprompt: 1,
        Login: new LoginDto(Enc("octo", _userKey), Enc("secret", _userKey), Enc("totp-seed", _userKey),
            [new LoginUriDto(Enc("https://github.com", _userKey), 0)]),
        Card: null, Identity: null, SecureNote: null, SshKey: null,
        Fields: null, CreationDate: DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
        RevisionDate: DateTimeOffset.Parse("2026-06-02T00:00:00Z"), DeletedDate: null);

    [Fact]
    public void Decrypt_Login_DecryptsAllFieldsAndFlags()
    {
        var result = NewDecryptor().Decrypt(Sync([LoginDto()]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal(CipherType.Login, cipher.Type);
        Assert.Equal("GitHub", cipher.Name);
        Assert.Equal("note", cipher.Notes);
        Assert.True(cipher.Favorite);
        Assert.True(cipher.Reprompt);
        Assert.Equal("f1", cipher.FolderId);
        Assert.False(cipher.IsDeleted);
        Assert.NotNull(cipher.Login);
        Assert.Equal("octo", cipher.Login!.Username);
        Assert.Equal("secret", cipher.Login.Password);
        Assert.Equal("totp-seed", cipher.Login.Totp);
        Assert.Equal("https://github.com", Assert.Single(cipher.Login.Uris).Uri);
    }

    [Fact]
    public void Decrypt_Card_Identity_SecureNote_Ssh_MapToTypedChildren()
    {
        var card = new CipherDto("c-card", (int)CipherType.Card, Enc("Visa", _userKey), null, null, null, null, false, 0,
            null, new CardDto(Enc("Jane", _userKey), Enc("4111", _userKey), Enc("12", _userKey), Enc("2030", _userKey), Enc("123", _userKey), Enc("Visa", _userKey)),
            null, null, null, null, null, null, null);
        var identity = new CipherDto("c-id", (int)CipherType.Identity, Enc("ID", _userKey), null, null, null, null, false, 0,
            null, null, new IdentityDto(Enc("Mr", _userKey), Enc("Jane", _userKey), null, Enc("Doe", _userKey), null, null, null, null, null,
                Enc("jane@example.com", _userKey), null, null, null, null, null, null, null, null),
            null, null, null, null, null, null);
        var note = new CipherDto("c-note", (int)CipherType.SecureNote, Enc("Note", _userKey), Enc("body", _userKey), null, null, null, false, 0,
            null, null, null, new SecureNoteDto(0), null, null, null, null, null);
        var ssh = new CipherDto("c-ssh", (int)CipherType.SshKey, Enc("Key", _userKey), null, null, null, null, false, 0,
            null, null, null, null, new SshKeyDto(Enc("priv", _userKey), Enc("pub", _userKey), Enc("fp", _userKey)),
            null, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([card, identity, note, ssh]), _userKey, "https://vault.example");

        var c = result.Ciphers.Single(x => x.Type == CipherType.Card);
        Assert.Equal("Jane", c.Card!.CardholderName);
        Assert.Equal("4111", c.Card.Number);
        Assert.Equal("Visa", c.Card.Brand);

        var id = result.Ciphers.Single(x => x.Type == CipherType.Identity);
        Assert.Equal("Jane", id.Identity!.FirstName);
        Assert.Equal("Doe", id.Identity.LastName);
        Assert.Equal("jane@example.com", id.Identity.Email);

        var n = result.Ciphers.Single(x => x.Type == CipherType.SecureNote);
        Assert.Equal(0, n.SecureNote!.Type);
        Assert.Equal("body", n.Notes);

        var s = result.Ciphers.Single(x => x.Type == CipherType.SshKey);
        Assert.Equal("priv", s.Ssh!.PrivateKey);
        Assert.Equal("pub", s.Ssh.PublicKey);
        Assert.Equal("fp", s.Ssh.Fingerprint);
    }

    [Fact]
    public void Decrypt_Folders_DecryptNames()
    {
        var folders = new[]
        {
            new FolderDto("f1", Enc("Work", _userKey), DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
            new FolderDto("f2", Enc("Personal", _userKey), DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
        };

        var result = NewDecryptor().Decrypt(Sync(folders: folders), _userKey, "https://vault.example");

        Assert.Equal(2, result.Folders.Count);
        Assert.Equal("Work", result.Folders[0].Name);
        Assert.Equal("Personal", result.Folders[1].Name);
    }

    [Fact]
    public void Decrypt_ItemLevelKey_OverridesUserKey()
    {
        var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var cipherKeyEnc = _crypto.Encrypt(itemKey.FullKey, _userKey).ToString();
        // These fields use the item key; using the user key would fail MAC validation.
        var dto = new CipherDto("c-key", (int)CipherType.Login, Enc("Secret Item", itemKey), null, cipherKeyEnc, null, null, false, 0,
            new LoginDto(Enc("alice", itemKey), Enc("pw", itemKey), null, null),
            null, null, null, null, null, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal("Secret Item", cipher.Name);
        Assert.Equal("alice", cipher.Login!.Username);
        Assert.Equal(0, result.SkippedCipherCount);
    }

    [Fact]
    public void Decrypt_CustomFields_PreserveTypeAndValue()
    {
        var fields = new[]
        {
            new FieldDto((int)CipherFieldType.Text, Enc("Plain", _userKey), Enc("v1", _userKey)),
            new FieldDto((int)CipherFieldType.Hidden, Enc("Secret", _userKey), Enc("v2", _userKey)),
            new FieldDto((int)CipherFieldType.Boolean, Enc("Flag", _userKey), Enc("true", _userKey)),
        };
        var dto = new CipherDto("c-fields", (int)CipherType.Login, Enc("WithFields", _userKey), null, null, null, null, false, 0,
            new LoginDto(null, null, null, null), null, null, null, null, fields, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal(3, cipher.Fields.Count);
        Assert.Equal(CipherFieldType.Text, cipher.Fields[0].Type);
        Assert.Equal("Plain", cipher.Fields[0].Name);
        Assert.Equal("v1", cipher.Fields[0].Value);
        Assert.Equal(CipherFieldType.Hidden, cipher.Fields[1].Type);
        Assert.Equal("v2", cipher.Fields[1].Value);
        Assert.Equal(CipherFieldType.Boolean, cipher.Fields[2].Type);
    }

    [Fact]
    public void Decrypt_BadCipher_IsSkippedGoodRemains()
    {
        var wrongKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var bad = new CipherDto("c-bad", (int)CipherType.Login, Enc("Bad", wrongKey), null, null, null, null, false, 0,
            new LoginDto(Enc("x", wrongKey), null, null, null), null, null, null, null, null, null, null, null);

        var result = NewDecryptor().Decrypt(Sync([bad, LoginDto()]), _userKey, "https://vault.example");

        var cipher = Assert.Single(result.Ciphers);
        Assert.Equal("GitHub", cipher.Name);
        Assert.Equal(1, result.SkippedCipherCount);
    }

    [Fact]
    public void Decrypt_DeletedDate_MarksTrash()
    {
        var dto = LoginDto() with { DeletedDate = DateTimeOffset.Parse("2026-06-10T00:00:00Z") };

        var result = NewDecryptor().Decrypt(Sync([dto]), _userKey, "https://vault.example");

        Assert.True(Assert.Single(result.Ciphers).IsDeleted);
    }

    [Fact]
    public void Decrypt_Account_FromProfileAndServer()
    {
        var result = NewDecryptor().Decrypt(Sync(profile: new ProfileDto("u1", "me@example.com", "Me", null, null)),
            _userKey, "https://vault.example");

        Assert.Equal("me@example.com", result.Account.Email);
        Assert.Equal("https://vault.example", result.Account.ServerUrl);
        Assert.Equal("M", result.Account.Initial);
    }
}
