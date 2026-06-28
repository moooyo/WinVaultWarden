using System.Text.Json;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

public class RequestWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PreloginRequest_uses_lowercase_email()
    {
        var json = JsonSerializer.Serialize(new PreloginRequest("a@b.com"), Web);
        Assert.Equal("""{"email":"a@b.com"}""", json);
    }

    [Fact]
    public void CipherRequest_is_camelCase_and_keeps_nulls()
    {
        var json = JsonSerializer.Serialize(new CipherRequest { Type = 1, Name = "n" }, Web);
        Assert.Contains("\"type\":1", json);
        Assert.Contains("\"name\":\"n\"", json);
        // Web 默认不忽略 null,login 等未设置字段必须写出为 null
        Assert.Contains("\"login\":null", json);
        Assert.Contains("\"notes\":null", json);
    }

    [Fact]
    public void FolderRequest_is_camelCase()
    {
        var json = JsonSerializer.Serialize(new FolderRequest { Name = "f" }, Web);
        Assert.Equal("""{"name":"f"}""", json);
    }
}
