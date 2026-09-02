using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Enums;
using Xunit;

namespace Api.Tests;

public class AttachmentDtoWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CipherAttachmentDto_reads_size_as_json_string()
    {
        // Vaultwarden Attachment.to_json emits size as a JSON string.
        const string json = """
        {"id":"att-1","fileName":"2.encName","key":"2.wrappedKey","size":"12345","sizeName":"12.06 KB","url":"https://files.example/att/att-1"}
        """;
        var dto = JsonSerializer.Deserialize<CipherAttachmentDto>(json, Web)!;
        Assert.Equal("att-1", dto.Id);
        Assert.Equal("2.encName", dto.FileName);
        Assert.Equal("2.wrappedKey", dto.Key);
        // Numeric conversion remains a Vault-layer concern.
        Assert.Equal("12345", dto.Size);
        Assert.Equal("12.06 KB", dto.SizeName);
        Assert.Equal("https://files.example/att/att-1", dto.Url);
    }

    [Fact]
    public void CipherAttachmentDto_legacy_attachment_has_null_key()
    {
        // Legacy attachments do not have an independent attachment key.
        const string json = """
        {"id":"att-2","fileName":"2.encName","key":null,"size":"7","sizeName":"7 Bytes","url":null}
        """;
        var dto = JsonSerializer.Deserialize<CipherAttachmentDto>(json, Web)!;
        Assert.Equal("att-2", dto.Id);
        Assert.Null(dto.Key);
        Assert.Null(dto.Url);
        Assert.Equal("7", dto.Size);
    }

    [Fact]
    public void CipherDto_reads_attachments_array()
    {
        const string json = """
        {"id":"c-1","type":1,"name":"2.name","attachments":[
          {"id":"att-1","fileName":"2.f1","key":"2.k1","size":"10","sizeName":"10 Bytes","url":"https://files/att-1"},
          {"id":"att-2","fileName":"2.f2","key":null,"size":"20","sizeName":"20 Bytes","url":"https://files/att-2"}
        ]}
        """;
        var dto = JsonSerializer.Deserialize<CipherDto>(json, Web)!;
        Assert.NotNull(dto.Attachments);
        Assert.Equal(2, dto.Attachments!.Length);
        Assert.Equal("att-1", dto.Attachments[0].Id);
        Assert.Equal("2.k1", dto.Attachments[0].Key);
        Assert.Null(dto.Attachments[1].Key);
    }

    [Fact]
    public void CipherDto_without_attachments_yields_null()
    {
        const string json = """{"id":"c-1","type":1,"name":"2.name"}""";
        var dto = JsonSerializer.Deserialize<CipherDto>(json, Web)!;
        Assert.Null(dto.Attachments);
    }

    [Fact]
    public void AttachmentUploadRequest_is_camelCase_with_numeric_fileSize_and_utc_revision()
    {
        var revision = new DateTime(2026, 6, 24, 1, 2, 3, 456, DateTimeKind.Utc);
        var req = new AttachmentUploadRequest("2.wrappedKey", "2.encName", 12345L, revision);
        var json = JsonSerializer.Serialize(req, Web);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("2.wrappedKey", root.GetProperty("key").GetString());
        Assert.Equal("2.encName", root.GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("fileSize").ValueKind);
        Assert.Equal(12345L, root.GetProperty("fileSize").GetInt64());
        Assert.Equal("2026-06-24T01:02:03.456Z", root.GetProperty("lastKnownRevisionDate").GetString());
    }

    [Fact]
    public void AttachmentUploadV2Response_reads_attachmentId_and_relative_url_ignores_cipherResponse()
    {
        // The client ignores the server's accompanying cipherResponse.
        const string json = """
        {"attachmentId":"att-1","url":"/ciphers/c-1/attachment/att-1","fileUploadType":0,"object":"attachment-fileUpload","cipherResponse":{"id":"c-1","object":"cipher"}}
        """;
        var dto = JsonSerializer.Deserialize<AttachmentUploadV2Response>(json, Web)!;
        Assert.Equal("att-1", dto.AttachmentId);
        Assert.Equal("/ciphers/c-1/attachment/att-1", dto.Url);
        Assert.Equal(FileUploadType.Direct, dto.FileUploadType);
    }

    [Fact]
    public void CipherAttachmentDto_context_matches_reflection_wire()
    {
        // Source-generated and reflection serialization must remain wire-equivalent for AOT.
        const string json = """
        {"id":"att-1","fileName":"2.encName","key":"2.k","size":"12345","sizeName":"12.06 KB","url":"https://files/att-1"}
        """;
        var reflect = JsonSerializer.Deserialize<CipherAttachmentDto>(json, Web)!;
        var sourceGen = JsonSerializer.Deserialize(json, ApiJsonContext.Default.CipherAttachmentDto)!;
        Assert.Equal(reflect, sourceGen);
    }

    [Fact]
    public void AttachmentUploadRequest_context_matches_reflection_wire()
    {
        var req = new AttachmentUploadRequest(
            "2.k",
            "2.encName",
            99L,
            new DateTime(2026, 6, 24, 1, 2, 3, DateTimeKind.Utc));
        Assert.Equal(
            JsonSerializer.Serialize(req, Web),
            JsonSerializer.Serialize(req, ApiJsonContext.Default.AttachmentUploadRequest));
    }
}
