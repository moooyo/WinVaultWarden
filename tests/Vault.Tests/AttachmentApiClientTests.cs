using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Api.Dtos;
using Core.Session;
using Xunit;

namespace Vault.Tests;

public class AttachmentApiClientTests
{
    private static ApiClient NewClient(FakeHttpMessageHandler handler)
    {
        var origin = new ServerOrigin("https://vault.example");
        var auth = new AuthHeaderHandler(
            origin,
            () => new AccessTokenBinding("account", origin, 1, "access-token"),
            _ => Task.FromResult(false))
        {
            InnerHandler = handler,
        };
        var client = new ApiClient(new HttpClient(auth), origin);
        return client;
    }

    private const string OneCipherJson =
        """
        {"id":"c-1","type":1,"name":"2.name","notes":null,"key":"2.cipherKey","organizationId":null,"folderId":null,"favorite":false,"reprompt":0,"login":null,"card":null,"identity":null,"secureNote":null,"sshKey":null,"fields":null,"attachments":[{"id":"att-1","fileName":"2.encName","key":"2.wrappedKey","size":"49","sizeName":"49 Bytes","url":"https://files.example/att/att-1"}],"creationDate":"2026-06-24T00:00:00Z","revisionDate":"2026-06-24T00:00:00Z","deletedDate":null}
        """;

    [Fact]
    public async Task GetCipher_GetsCipherByIdWithAttachments()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, OneCipherJson));
        var client = NewClient(handler);

        var dto = await client.GetCipherAsync("c-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/c-1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("c-1", dto.Id);
        Assert.NotNull(dto.Attachments);
        Assert.Single(dto.Attachments!);
        Assert.Equal("att-1", dto.Attachments![0].Id);
        Assert.Equal("2.wrappedKey", dto.Attachments[0].Key);
        Assert.Equal("49", dto.Attachments[0].Size);
        Assert.Equal("https://files.example/att/att-1", dto.Attachments[0].Url);
    }

    [Fact]
    public async Task CreateAttachmentV2_PostsToAttachmentV2WithCamelCaseBody()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"attachmentId":"att-1","url":"/ciphers/c-1/attachment/att-1","fileUploadType":0,"object":"attachment-fileUpload","cipherResponse":{"id":"c-1","object":"cipher"}}"""));
        var client = NewClient(handler);

        var receipt = await client.CreateAttachmentV2Async(
            "c-1", new AttachmentUploadRequest(
                "2.wrappedKey",
                "2.encName",
                49L,
                new DateTime(2026, 6, 24, 1, 2, 3, 456, DateTimeKind.Utc)),
            TestContext.Current.CancellationToken);
        var resp = receipt.Value;

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/c-1/attachment/v2", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"key\":\"2.wrappedKey\"", handler.Bodies[0]);
        Assert.Contains("\"fileName\":\"2.encName\"", handler.Bodies[0]);
        Assert.Contains("\"fileSize\":49", handler.Bodies[0]);
        Assert.Contains(
            "\"lastKnownRevisionDate\":\"2026-06-24T01:02:03.456Z\"",
            handler.Bodies[0]);
        Assert.Equal("att-1", resp.AttachmentId);
        Assert.Equal("/ciphers/c-1/attachment/att-1", resp.Url);
        Assert.Equal("access-token", receipt.Authorization.Token);
    }

    [Theory]
    [InlineData("/ciphers/c-1/attachment/att-1")]
    [InlineData("c-1/attachment/att-1")]
    public async Task UploadAttachmentData_NormalizesSupportedDirectUploadPaths(
        string uploadUrl)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        var upload = new StreamUploadContent(
            4,
            (output, ct) => output.WriteAsync(new byte[] { 2, 9, 9, 9 }, ct).AsTask());

        await client.UploadAttachmentDataAsync(
            FileUploadType.Direct,
            uploadUrl,
            "c-1",
            "att-1",
            "2.encName",
            upload,
            TestContext.Current.CancellationToken);

        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("access-token", req.Headers.Authorization!.Parameter);
        Assert.Equal("/api/ciphers/c-1/attachment/att-1", req.RequestUri!.AbsolutePath);
        Assert.StartsWith("multipart/form-data", req.Content!.Headers.ContentType!.ToString());
        Assert.Contains("name=data", handler.Bodies[0].Replace("\"", ""));
        Assert.Contains("filename=2.encName", handler.Bodies[0].Replace("\"", ""));
    }

    [Fact]
    public async Task DeleteAttachment_DeletesAttachmentSubpath()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);

        await client.DeleteAttachmentAsync("c-1", "att-1", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
        Assert.Equal("/api/ciphers/c-1/attachment/att-1", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task OpenAttachmentDownload_GetsStreamFromAbsoluteUrl()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 2, 1, 2, 3 }),
        });
        var client = NewClient(handler);

        await using var download = await client.OpenAttachmentDownloadAsync(
            "https://files.example/att/att-1?t=tok", TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await download.Stream.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/att/att-1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("files.example", handler.Requests[0].RequestUri!.Host);
        Assert.Equal(new byte[] { 2, 1, 2, 3 }, copy.ToArray());
    }

    [Fact]
    public async Task CreateAttachmentV2_WriteError_400_ThrowsVaultWriteException()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            """{"message":"Cipher not found","validationErrors":{},"object":"error"}"""));
        var client = NewClient(handler);

        var ex = await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.CreateAttachmentV2Async(
                "c-1", new AttachmentUploadRequest(
                    "2.k",
                    "2.encName",
                    49L,
                    new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc)),
                TestContext.Current.CancellationToken));

        Assert.Equal("Cipher not found", ex.Message);
    }
}
