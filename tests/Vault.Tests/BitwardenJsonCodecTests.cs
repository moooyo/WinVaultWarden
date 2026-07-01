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
    public void Parse_MalformedJson_Throws() =>
        Assert.ThrowsAny<Exception>(() => BitwardenJsonCodec.Parse("{ not json"));

    [Fact]
    public void Parse_EmptyItems_ReturnsEmpty()
    {
        var parsed = BitwardenJsonCodec.Parse("{\"encrypted\":false,\"folders\":[],\"items\":[]}");
        Assert.Empty(parsed.Ciphers);
        Assert.Empty(parsed.Folders);
    }
}
