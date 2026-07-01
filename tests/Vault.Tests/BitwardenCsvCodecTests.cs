using Core.Enums;
using Core.Models;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public class BitwardenCsvCodecTests
{
    [Fact]
    public void RoundTrip_Login_WithEscaping()
    {
        var c = new Cipher { Id="1", Type=CipherType.Login, Name="A, Inc", Notes="line1\nline2", Favorite=true, FolderId="f1",
            Login = new CipherLogin("u\"q", "p,w", "totp", new[]{ new CipherLoginUri("https://x.com", null) }) };
        var folders = new[]{ new Folder{ Id="f1", Name="Work" } };
        var csv = BitwardenCsvCodec.Serialize(new[]{c}, folders);
        Assert.StartsWith("folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp", csv);

        var parsed = BitwardenCsvCodec.Parse(csv);
        var p = Assert.Single(parsed.Ciphers);
        Assert.Equal("A, Inc", p.Name);              // 逗号经引号转义还原
        Assert.Equal("line1\nline2", p.Notes);       // 换行
        Assert.Equal("u\"q", p.Login!.Username);      // 引号翻倍
        Assert.Equal("p,w", p.Login.Password);
        Assert.Equal("https://x.com", p.Login.Uris[0].Uri);
        Assert.Equal("Work", Assert.Single(parsed.Folders).Name);
        Assert.Equal((0,0), Assert.Single(parsed.Relations));
    }

    [Fact]
    public void RoundTrip_Note_LossyNonLogin()
    {
        var c = new Cipher { Id="1", Type=CipherType.SecureNote, Name="N", Notes="body", SecureNote=new CipherSecureNote(0) };
        var parsed = BitwardenCsvCodec.Parse(BitwardenCsvCodec.Serialize(new[]{c}, Array.Empty<Folder>()));
        var p = Assert.Single(parsed.Ciphers);
        Assert.Equal(CipherType.SecureNote, p.Type);
        Assert.Equal("body", p.Notes);
        Assert.Null(p.Login);
    }

    [Fact]
    public void Parse_HeaderOnly_Empty() =>
        Assert.Empty(BitwardenCsvCodec.Parse("folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp").Ciphers);
}
