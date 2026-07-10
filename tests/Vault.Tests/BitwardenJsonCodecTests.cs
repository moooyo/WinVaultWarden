using Core.Enums;
using Core.Models;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public class BitwardenJsonCodecTests
{
    private static Cipher Login() => new()
    {
        Id = "c1", Type = CipherType.Login, Name = "GitHub", Notes = "n", Favorite = true, FolderId = "f1",
        Login = new CipherLogin("u", "p", "totp", new[] { new CipherLoginUri("https://github.com", 0) }),
        Fields = new[] { new CipherField("k", "v", CipherFieldType.Hidden) },
        PasswordHistory = new[] { new PasswordHistoryEntry("old", new DateTimeOffset(2026,1,2,3,4,5,TimeSpan.Zero)) },
    };

    [Fact]
    public void RoundTrip_LoginWithFolderFieldsHistory()
    {
        var folders = new[] { new Folder { Id = "f1", Name = "Work" } };
        var json = BitwardenJsonCodec.Serialize(new[] { Login() }, folders);
        Assert.Contains("\"encrypted\":false", json);

        var parsed = BitwardenJsonCodec.Parse(json);
        var c = Assert.Single(parsed.Ciphers);
        Assert.Equal(CipherType.Login, c.Type);
        Assert.Equal("GitHub", c.Name);
        Assert.Equal("p", c.Login!.Password);
        Assert.Equal("https://github.com", c.Login.Uris[0].Uri);
        Assert.Equal("v", c.Fields[0].Value);
        Assert.Equal(CipherFieldType.Hidden, c.Fields[0].Type);
        Assert.Equal("old", c.PasswordHistory[0].Password);
        Assert.Equal("Work", Assert.Single(parsed.Folders).Name);
        Assert.Equal((0, 0), Assert.Single(parsed.Relations)); // cipher0 → folder0
    }

    [Fact]
    public void RoundTrip_Card()
    {
        var c = new Cipher { Id="1", Type=CipherType.Card, Name="Visa",
            Card = new CipherCard("Zhang","4111","08","28","123","Visa") };
        var parsed = BitwardenJsonCodec.Parse(BitwardenJsonCodec.Serialize(new[]{c}, Array.Empty<Folder>()));
        var card = Assert.Single(parsed.Ciphers).Card!;
        Assert.Equal("4111", card.Number);
        Assert.Equal("123", card.Code);
    }

    [Fact]
    public void RoundTrip_Identity_Note_Ssh()
    {
        var items = new[] {
            new Cipher { Id="1", Type=CipherType.Identity, Name="Id", Identity = new CipherIdentity("Mr","F","M","L","u","co","ssn","pp","lic","e@x","p","a1","a2","a3","city","st","zip","cn") },
            new Cipher { Id="2", Type=CipherType.SecureNote, Name="Note", Notes="body", SecureNote = new CipherSecureNote(0) },
            new Cipher { Id="3", Type=CipherType.SshKey, Name="Key", Ssh = new CipherSsh("priv","pub","fp") },
        };
        var parsed = BitwardenJsonCodec.Parse(BitwardenJsonCodec.Serialize(items, Array.Empty<Folder>()));
        Assert.Equal("F", parsed.Ciphers[0].Identity!.FirstName);
        Assert.Equal(CipherType.SecureNote, parsed.Ciphers[1].Type);
        Assert.Equal("priv", parsed.Ciphers[2].Ssh!.PrivateKey);
    }

    [Fact]
    public void RoundTrip_LoginWithFido2Credentials_PreservesEveryField()
    {
        var created = DateTimeOffset.Parse("2026-06-24T00:00:00Z");
        var credential = new CipherFido2Credential(
            "credential-id",
            "public-key",
            "ECDSA",
            "P-256",
            "private-key-pkcs8",
            "example.com",
            "user-handle",
            "user@example.com",
            42,
            "Example",
            "Example User",
            true,
            created);
        var cipher = new Cipher
        {
            Id = "c1",
            Type = CipherType.Login,
            Name = "Example",
            Login = new CipherLogin("user@example.com", "password", null, [])
            {
                Fido2Credentials = [credential],
            },
        };

        var parsed = BitwardenJsonCodec.Parse(
            BitwardenJsonCodec.Serialize([cipher], []));
        var actual = Assert.Single(
            Assert.Single(parsed.Ciphers).Login!.Fido2Credentials);

        Assert.Equal(credential, actual);
    }

    [Fact]
    public void Serialize_Fido2Credential_WritesWireFieldsAsSpecified()
    {
        var created = DateTimeOffset.Parse("2026-06-24T00:00:00Z");
        var credential = new CipherFido2Credential(
            "credential-id",
            "public-key",
            "ECDSA",
            "P-256",
            "private-key-pkcs8",
            "example.com",
            "user-handle",
            "user@example.com",
            42,
            "Example",
            "Example User",
            true,
            created);
        var cipher = new Cipher
        {
            Id = "c1",
            Type = CipherType.Login,
            Name = "Example",
            Login = new CipherLogin("user@example.com", "password", null, [])
            {
                Fido2Credentials = [credential],
            },
        };

        var json = BitwardenJsonCodec.Serialize([cipher], []);

        // counter 与 discoverable 在 wire 上是字符串（分别为十进制数字与 "true"/"false"），
        // creationDate 精确保留；私钥（keyValue）以明文写入未加密导出——这是用户显式选择的备份内容。
        Assert.Contains("\"credentialId\":\"credential-id\"", json);
        Assert.Contains("\"keyValue\":\"private-key-pkcs8\"", json);
        Assert.Contains("\"rpId\":\"example.com\"", json);
        Assert.Contains("\"userHandle\":\"user-handle\"", json);
        Assert.Contains("\"userName\":\"user@example.com\"", json);
        Assert.Contains("\"counter\":\"42\"", json);
        Assert.Contains("\"rpName\":\"Example\"", json);
        Assert.Contains("\"userDisplayName\":\"Example User\"", json);
        Assert.Contains("\"discoverable\":\"true\"", json);
        Assert.Contains("\"creationDate\":\"2026-06-24T00:00:00", json);
    }

    [Fact]
    public void Parse_EncryptedTrue_ThrowsActionableException()
    {
        var ex = Assert.Throws<EncryptedBitwardenJsonNotSupportedException>(() =>
            BitwardenJsonCodec.Parse(
                "{\"encrypted\":true,\"folders\":[],\"items\":[]}"));

        Assert.Equal(
            "不支持加密的 Bitwarden JSON；请先导出未加密 JSON 后重试。",
            ex.Message);
    }

    [Fact]
    public void Parse_MalformedJson_Throws() =>
        Assert.ThrowsAny<Exception>(() => BitwardenJsonCodec.Parse("{ not json"));

    [Fact]
    public void Parse_EmptyItems_ReturnsEmpty()
    {
        var parsed = BitwardenJsonCodec.Parse("{\"encrypted\":false,\"folders\":[],\"items\":[]}");
        Assert.Empty(parsed.Ciphers);
        Assert.Empty(parsed.Folders);
    }

    [Fact]
    public void Parse_UnknownCipherType_DefaultsToLogin()
    {
        var json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"name\":\"Weird\",\"type\":99}]}";

        var exception = Record.Exception(() => BitwardenJsonCodec.Parse(json));

        Assert.Null(exception);
        var parsed = BitwardenJsonCodec.Parse(json);
        var cipher = Assert.Single(parsed.Ciphers);
        Assert.Equal(CipherType.Login, cipher.Type);
        Assert.Equal("Weird", cipher.Name);
    }
}
