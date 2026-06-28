using System.Text.Json;
using Api;
using Api.Dtos;
using Xunit;

namespace Vault.Tests;

public class SendDtoTests
{
    [Fact]
    public void SendListResponse_DeserializesTextAndFileSends()
    {
        var json =
            """
            {
              "data": [
                {
                  "id": "s-text",
                  "accessId": "abc123access",
                  "type": 1,
                  "name": "2.name",
                  "notes": "2.notes",
                  "text": { "text": "2.secret", "hidden": false },
                  "file": null,
                  "key": "2.wrappedseed",
                  "maxAccessCount": 5,
                  "accessCount": 2,
                  "password": "proofhash",
                  "authType": 1,
                  "disabled": false,
                  "hideEmail": true,
                  "revisionDate": "2026-06-24T00:00:00Z",
                  "expirationDate": "2026-07-01T00:00:00Z",
                  "deletionDate": "2026-07-10T00:00:00Z",
                  "object": "send"
                },
                {
                  "id": "s-file",
                  "accessId": "def456access",
                  "type": 2,
                  "name": "2.filesend",
                  "notes": null,
                  "text": null,
                  "file": { "id": "file-1", "fileName": "2.report.pdf", "size": "20480", "sizeName": "20 KB" },
                  "key": "2.wrappedseed2",
                  "maxAccessCount": null,
                  "accessCount": 0,
                  "password": null,
                  "authType": 0,
                  "disabled": true,
                  "hideEmail": false,
                  "revisionDate": "2026-06-24T00:00:00Z",
                  "expirationDate": null,
                  "deletionDate": "2026-07-10T00:00:00Z",
                  "object": "send"
                }
              ],
              "object": "list",
              "continuationToken": null
            }
            """;

        var list = JsonSerializer.Deserialize(json, ApiJsonContext.Default.SendListResponse)!;

        Assert.Equal("list", list.Object);
        Assert.Equal(2, list.Data.Length);

        var text = list.Data[0];
        Assert.Equal("s-text", text.Id);
        Assert.Equal("abc123access", text.AccessId);
        Assert.Equal(1, text.Type);
        Assert.Equal("2.name", text.Name);
        Assert.Equal("2.notes", text.Notes);
        Assert.NotNull(text.Text);
        Assert.Equal("2.secret", text.Text!.Text);
        Assert.False(text.Text.Hidden);
        Assert.Null(text.File);
        Assert.Equal("2.wrappedseed", text.Key);
        Assert.Equal(5, text.MaxAccessCount);
        Assert.Equal(2, text.AccessCount);
        Assert.Equal("proofhash", text.Password);
        Assert.Equal(1, text.AuthType);
        Assert.True(text.HideEmail);
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T00:00:00Z"), text.ExpirationDate);
        Assert.Equal(DateTimeOffset.Parse("2026-07-10T00:00:00Z"), text.DeletionDate);

        var file = list.Data[1];
        Assert.Equal("s-file", file.Id);
        Assert.Equal(2, file.Type);
        Assert.Null(file.Text);
        Assert.NotNull(file.File);
        Assert.Equal("file-1", file.File!.Id);
        Assert.Equal("2.report.pdf", file.File.FileName);
        Assert.Equal(20480, file.File.Size);          // size arrives as a string -> long?
        Assert.Equal("20 KB", file.File.SizeName);
        Assert.Null(file.Password);
        Assert.True(file.Disabled);
        Assert.Null(file.MaxAccessCount);
        Assert.Null(file.ExpirationDate);
    }

    [Fact]
    public void SendRequest_SerializesCamelCaseBody()
    {
        var request = new SendRequest
        {
            Type = 1,
            Key = "2.wrappedseed",
            Password = "proofhash",
            MaxAccessCount = 5,
            ExpirationDate = "2026-07-01T00:00:00Z",
            DeletionDate = "2026-07-10T00:00:00Z",
            Disabled = false,
            HideEmail = true,
            Name = "2.name",
            Notes = "2.notes",
            Text = new SendTextRequest("2.secret", false),
            File = null,
            FileLength = null,
            Id = null,
        };

        var body = JsonSerializer.Serialize(request, ApiJsonContext.Default.SendRequest);

        Assert.Contains("\"type\":1", body);
        Assert.Contains("\"key\":\"2.wrappedseed\"", body);
        Assert.Contains("\"password\":\"proofhash\"", body);
        Assert.Contains("\"maxAccessCount\":5", body);
        Assert.Contains("\"deletionDate\":\"2026-07-10T00:00:00Z\"", body);
        Assert.Contains("\"hideEmail\":true", body);
        Assert.Contains("\"name\":\"2.name\"", body);
        Assert.Contains("\"text\":{", body);
        Assert.Contains("\"text\":\"2.secret\"", body);
        Assert.Contains("\"hidden\":false", body);
    }

    [Fact]
    public void SendFileUploadV2Response_DeserializesUrlAndSendResponse()
    {
        var json =
            """
            {
              "fileUploadType": 0,
              "object": "send-fileUpload",
              "url": "/sends/s-file/file/file-1",
              "sendResponse": {
                "id": "s-file",
                "accessId": "def456access",
                "type": 2,
                "name": "2.filesend",
                "notes": null,
                "text": null,
                "file": { "id": "file-1", "fileName": "2.report.pdf", "size": "20480", "sizeName": "20 KB" },
                "key": "2.wrappedseed2",
                "maxAccessCount": null,
                "accessCount": 0,
                "password": null,
                "authType": 0,
                "disabled": false,
                "hideEmail": false,
                "revisionDate": "2026-06-24T00:00:00Z",
                "expirationDate": null,
                "deletionDate": "2026-07-10T00:00:00Z",
                "object": "send"
              }
            }
            """;

        var upload = JsonSerializer.Deserialize(json, ApiJsonContext.Default.SendFileUploadV2Response)!;

        Assert.Equal(0, upload.FileUploadType);
        Assert.Equal("/sends/s-file/file/file-1", upload.Url);
        Assert.Equal("s-file", upload.SendResponse.Id);
        Assert.Equal("file-1", upload.SendResponse.File!.Id);
    }
}
