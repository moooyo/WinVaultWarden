using System.Net;
using Api;
using Api.Dtos;
using Xunit;

namespace Vault.Tests;

public class SendApiClientTests
{
    private static ApiClient NewClient(FakeHttpMessageHandler handler)
    {
        var client = new ApiClient(new HttpClient(handler));
        client.SetBaseAddress("https://vault.example");
        return client;
    }

    private const string OneSendListJson =
        """
        {"data":[{"id":"s1","accessId":"acc1","type":1,"name":"2.n","notes":null,"text":{"text":"2.t","hidden":false},"file":null,"key":"2.k","maxAccessCount":null,"accessCount":0,"password":null,"authType":0,"disabled":false,"hideEmail":false,"revisionDate":"2026-06-24T00:00:00Z","expirationDate":null,"deletionDate":"2026-07-10T00:00:00Z","object":"send"}],"object":"list","continuationToken":null}
        """;

    private const string OneSendJson =
        """
        {"id":"s1","accessId":"acc1","type":1,"name":"2.n","notes":null,"text":{"text":"2.t","hidden":false},"file":null,"key":"2.k","maxAccessCount":null,"accessCount":0,"password":null,"authType":0,"disabled":false,"hideEmail":false,"revisionDate":"2026-06-24T00:00:00Z","expirationDate":null,"deletionDate":"2026-07-10T00:00:00Z","object":"send"}
        """;

    private static SendRequest SampleText() => new()
    {
        Type = 1,
        Key = "2.k",
        Name = "2.n",
        DeletionDate = "2026-07-10T00:00:00Z",
        Text = new SendTextRequest("2.t", false),
    };

    private static SendRequest SampleFile() => new()
    {
        Type = 2,
        Key = "2.k",
        Name = "2.n",
        DeletionDate = "2026-07-10T00:00:00Z",
        File = new SendFileRequest("2.report.pdf"),
        FileLength = 49,
    };

    [Fact]
    public async Task GetSends_GetsSendsList()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, OneSendListJson));
        var client = NewClient(handler);

        var list = await client.GetSendsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/sends", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Single(list.Data);
        Assert.Equal("s1", list.Data[0].Id);
    }

    [Fact]
    public async Task CreateTextSend_PostsToSendsWithCamelCaseBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, OneSendJson));
        var client = NewClient(handler);

        var created = await client.CreateTextSendAsync(SampleText(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/sends", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"type\":1", handler.Bodies[0]);
        Assert.Contains("\"name\":\"2.n\"", handler.Bodies[0]);
        Assert.Contains("\"text\":{", handler.Bodies[0]);
        Assert.Equal("s1", created.Id);
    }

    [Fact]
    public async Task CreateFileSendV2_PostsToFileV2AndParsesUploadUrl()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            $$"""
            {"fileUploadType":0,"object":"send-fileUpload","url":"/sends/s1/file/file-1","sendResponse":{{OneSendJson}}}
            """));
        var client = NewClient(handler);

        var upload = await client.CreateFileSendV2Async(SampleFile(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/sends/file/v2", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"type\":2", handler.Bodies[0]);
        Assert.Contains("\"fileLength\":49", handler.Bodies[0]);
        Assert.Contains("\"file\":{", handler.Bodies[0]);
        Assert.Equal("/sends/s1/file/file-1", upload.Url);
        Assert.Equal("s1", upload.SendResponse.Id);
    }

    [Fact]
    public async Task UploadSendFile_PostsMultipartWithDataPartNamedByEncryptedFileName()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        var buffer = new byte[] { 2, 9, 9, 9 };

        await client.UploadSendFileAsync(
            "/sends/s1/file/file-1", "2.report.pdf", buffer, TestContext.Current.CancellationToken);

        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        // Vaultwarden v2 upload URL "/sends/{id}/file/{fid}" は /api 前缀なし。
        // ApiClient.NormalizeServerPath が自動補完して "/api/sends/..." になる。
        Assert.Equal("/api/sends/s1/file/file-1", req.RequestUri!.AbsolutePath);
        Assert.StartsWith("multipart/form-data", req.Content!.Headers.ContentType!.MediaType is { } m ? req.Content.Headers.ContentType.ToString() : "");
        // body contains the multipart "data" part whose filename == encrypted file name
        Assert.Contains("name=data", handler.Bodies[0].Replace("\"", ""));
        Assert.Contains("filename=2.report.pdf", handler.Bodies[0].Replace("\"", ""));
    }

    [Fact]
    public async Task UpdateSend_PutsToSendId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, OneSendJson));
        var client = NewClient(handler);

        var updated = await client.UpdateSendAsync("s1", SampleText(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/sends/s1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"name\":\"2.n\"", handler.Bodies[0]);
        Assert.Equal("s1", updated.Id);
    }

    [Fact]
    public async Task DeleteSend_DeletesSendId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.DeleteSendAsync("s1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api/sends/s1", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RemoveSendPassword_PutsToRemovePasswordSubpath()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, OneSendJson));
        var client = NewClient(handler);

        var result = await client.RemoveSendPasswordAsync("s1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Equal("/api/sends/s1/remove-password", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("s1", result.Id);
    }

    [Fact]
    public async Task AccessSend_PostsToAccessIdWithPasswordBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"s1","type":1,"name":"2.n","text":{"text":"2.t","hidden":false},"file":null,"expirationDate":null,"creatorIdentifier":"me@example.com","object":"send-access"}"""));
        var client = NewClient(handler);

        var accessed = await client.AccessSendAsync("acc1", "proofhash", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/sends/access/acc1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"password\":\"proofhash\"", handler.Bodies[0]);
        Assert.Equal("2.t", accessed.Text!.Text);
        Assert.Equal("me@example.com", accessed.CreatorIdentifier);
    }

    [Fact]
    public async Task DownloadSendFileBytes_GetsBytesFromUrl()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 2, 1, 2, 3 }),
        });
        var client = NewClient(handler);

        var bytes = await client.DownloadSendFileBytesAsync(
            "https://vault.example/api/sends/s1/file-1?t=tok", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/sends/s1/file-1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(new byte[] { 2, 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task WriteError_400_ThrowsVaultWriteException()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"message":"Send not found","validationErrors":{},"object":"error"}"""));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.UpdateSendAsync("s1", SampleText(), TestContext.Current.CancellationToken));

        Assert.Equal("Send not found", ex.Message);
    }
}
