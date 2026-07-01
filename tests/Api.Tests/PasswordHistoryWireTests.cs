using System.Text.Json;
using Api;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

public class PasswordHistoryWireTests
{
    [Fact]
    public void CipherDto_DeserializesPasswordHistory_CamelCase()
    {
        const string json = """
        {"id":"c1","type":1,"name":"2.n","passwordHistory":[
          {"password":"2.old","lastUsedDate":"2026-01-02T03:04:05.000Z"}]}
        """;
        var dto = JsonSerializer.Deserialize(json, ApiJsonContext.Default.CipherDto)!;
        var h = Assert.Single(dto.PasswordHistory!);
        Assert.Equal("2.old", h.Password);
        Assert.Equal("2026-01-02T03:04:05.000Z", h.LastUsedDate);
    }

    [Fact]
    public void CipherRequest_SerializesPasswordHistory_CamelCase()
    {
        var req = new CipherRequest
        {
            Type = 1, Name = "2.n",
            PasswordHistory = new[] { new PasswordHistoryRequest { Password = "2.old", LastUsedDate = "2026-01-02T03:04:05.0000000Z" } },
        };
        var json = JsonSerializer.Serialize(req, ApiJsonContext.Default.CipherRequest);
        Assert.Contains("\"passwordHistory\":[", json);
        Assert.Contains("\"password\":\"2.old\"", json);
        Assert.Contains("\"lastUsedDate\":\"2026-01-02T03:04:05.0000000Z\"", json);
    }
}
