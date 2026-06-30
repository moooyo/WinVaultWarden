using System.Text.Json;
using Api;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

public class EmergencyAccessWireTests
{
    [Fact]
    public void GranteeDetails_DeserializesCamelCase()
    {
        const string json = """
        {"id":"e1","status":2,"type":1,"waitTimeDays":7,"granteeId":"u2",
         "email":"b@x.com","name":"B","avatarColor":"#fff","object":"emergencyAccessGranteeDetails"}
        """;
        var dto = JsonSerializer.Deserialize(json, ApiJsonContext.Default.EmergencyAccessGranteeDetailsDto)!;
        Assert.Equal("e1", dto.Id);
        Assert.Equal(2, dto.Status);
        Assert.Equal(1, dto.Type);
        Assert.Equal(7, dto.WaitTimeDays);
        Assert.Equal("u2", dto.GranteeId);
        Assert.Equal("b@x.com", dto.Email);
    }

    [Fact]
    public void InviteRequest_SerializesCamelCase()
    {
        var json = JsonSerializer.Serialize(
            new EmergencyAccessInviteRequest { Email = "b@x.com", Type = 1, WaitTimeDays = 7 },
            ApiJsonContext.Default.EmergencyAccessInviteRequest);
        Assert.Contains("\"email\":\"b@x.com\"", json);
        Assert.Contains("\"type\":1", json);
        Assert.Contains("\"waitTimeDays\":7", json);
    }

    [Fact]
    public void TakeoverResponse_DeserializesKdfFields()
    {
        const string json = """
        {"kdf":0,"kdfIterations":600000,"kdfMemory":null,"kdfParallelism":null,
         "keyEncrypted":"4.abc","object":"emergencyAccessTakeover"}
        """;
        var dto = JsonSerializer.Deserialize(json, ApiJsonContext.Default.EmergencyAccessTakeoverResponse)!;
        Assert.Equal(0, dto.Kdf);
        Assert.Equal(600000, dto.KdfIterations);
        Assert.Null(dto.KdfMemory);
        Assert.Equal("4.abc", dto.KeyEncrypted);
    }
}
