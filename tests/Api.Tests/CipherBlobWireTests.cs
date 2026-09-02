using System.Text.Json;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

public sealed class CipherBlobWireTests
{
    [Fact]
    public void CipherRequest_SerializesOpaqueDataAndNullLegacyFields()
    {
        var request = new CipherRequest
        {
            Type = 2,
            EncryptedFor = "00112233-4455-6677-8899-aabbccddeeff",
            EncryptedByKeyId = "00112233445566778899aabbccddeeff",
            Name = null,
            Key = "7.item-key",
            Data = "{\"format_version\":1}",
        };

        var json = JsonSerializer.Serialize(
            request,
            ApiJsonContext.Default.CipherRequest);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("name").ValueKind);
        Assert.Equal(
            "00112233-4455-6677-8899-aabbccddeeff",
            document.RootElement.GetProperty("encryptedFor").GetString());
        Assert.Equal(
            "00112233445566778899aabbccddeeff",
            document.RootElement.GetProperty("encryptedByKeyId").GetString());
        Assert.Equal("7.item-key", document.RootElement.GetProperty("key").GetString());
        Assert.Equal(
            "{\"format_version\":1}",
            document.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public void CipherDto_DeserializesOpaqueData()
    {
        const string json =
            "{\"id\":\"cipher-1\",\"type\":2,\"name\":null,\"notes\":null,\"key\":\"7.item-key\",\"organizationId\":null,\"folderId\":null,\"favorite\":false,\"reprompt\":0,\"login\":null,\"card\":null,\"identity\":null,\"secureNote\":null,\"sshKey\":null,\"fields\":null,\"creationDate\":null,\"revisionDate\":null,\"deletedDate\":null,\"data\":\"{\\\"format_version\\\":1}\"}";

        var dto = JsonSerializer.Deserialize(json, ApiJsonContext.Default.CipherDto);

        Assert.NotNull(dto);
        Assert.Equal("{\"format_version\":1}", dto.Data);
    }

    [Fact]
    public void CipherWithIdRequest_CopyPreservesEncryptionContext()
    {
        var source = new CipherRequest
        {
            Type = 2,
            EncryptedFor = "00112233-4455-6677-8899-aabbccddeeff",
            EncryptedByKeyId = "00112233445566778899aabbccddeeff",
            Name = null,
            Key = "7.item-key",
            Data = "{\"format_version\":1}",
        };

        var request = new CipherWithIdRequest("cipher-1", source);
        var json = JsonSerializer.Serialize(
            request,
            ApiJsonContext.Default.CipherWithIdRequest);
        using var document = JsonDocument.Parse(json);

        Assert.Equal("cipher-1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal(
            source.EncryptedFor,
            document.RootElement.GetProperty("encryptedFor").GetString());
        Assert.Equal(
            source.EncryptedByKeyId,
            document.RootElement.GetProperty("encryptedByKeyId").GetString());
        Assert.Equal(source.Data, document.RootElement.GetProperty("data").GetString());
    }
}
