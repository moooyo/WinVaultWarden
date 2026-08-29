using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Core.Session;
using Api.Dtos;
using Xunit;

namespace Vault.Tests;

public class SendApiClientTests
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

    private static IPublicSendApiClient NewAnonymousClient(FakeHttpMessageHandler handler) =>
        new ApiClientFactory(_ => handler).CreateAnonymous<IPublicSendApiClient>(
            ServerEndpoints.ForSelfHosted("https://vault.example"));

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
        Type = 0,
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
        Assert.Contains("\"type\":0", handler.Bodies[0]);
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

        var receipt = await client.CreateFileSendV2Async(SampleFile(), TestContext.Current.CancellationToken);
        var upload = receipt.Value;

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("/api/sends/file/v2", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"type\":2", handler.Bodies[0]);
        Assert.Contains("\"fileLength\":49", handler.Bodies[0]);
        Assert.Contains("\"file\":{", handler.Bodies[0]);
        Assert.Equal("/sends/s1/file/file-1", upload.Url);
        Assert.Equal("s1", upload.SendResponse.Id);
        Assert.Equal("access-token", receipt.Authorization.Token);
    }

    [Fact]
    public async Task CreateFileSendV2_After401Refresh_ReceiptsTokenUsedBySuccessfulRetry()
    {
        var origin = new ServerOrigin("https://vault.example");
        var token = "old-token";
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            $$"""
            {"fileUploadType":0,"object":"send-fileUpload","url":"/sends/s1/file/file-1","sendResponse":{{OneSendJson}}}
            """));
        var auth = new AuthHeaderHandler(
            origin,
            () => new AccessTokenBinding("account", origin, 7, token),
            _ =>
            {
                token = "refreshed-token";
                return Task.FromResult(true);
            })
        {
            InnerHandler = handler,
        };
        var client = new ApiClient(new HttpClient(auth), origin);

        var receipt = await client.CreateFileSendV2Async(
            SampleFile(),
            TestContext.Current.CancellationToken);

        Assert.Equal("s1", receipt.Value.SendResponse.Id);
        Assert.Equal("refreshed-token", receipt.Authorization.Token);
        Assert.Equal("old-token", handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal("refreshed-token", handler.Requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task UploadSendFile_PostsMultipartWithDataPartNamedByEncryptedFileName()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var client = NewClient(handler);
        var upload = new StreamUploadContent(
            4, (output, ct) => output.WriteAsync(new byte[] { 2, 9, 9, 9 }, ct).AsTask());

        await client.UploadSendFileAsync(
            FileUploadType.Direct,
            "/sends/s1/file/file-1",
            "s1",
            "file-1",
            "2.report.pdf",
            upload,
            TestContext.Current.CancellationToken);

        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("access-token", req.Headers.Authorization!.Parameter);
        // Vaultwarden v2 upload URL "/sends/{id}/file/{fid}" は /api 前缀なし。
        // ApiClient.NormalizeServerPath が自動補完して "/api/sends/..." になる。
        Assert.Equal("/api/sends/s1/file/file-1", req.RequestUri!.AbsolutePath);
        Assert.StartsWith("multipart/form-data", req.Content!.Headers.ContentType!.MediaType is { } m ? req.Content.Headers.ContentType.ToString() : "");
        // body contains the multipart "data" part whose filename == encrypted file name
        Assert.Contains("name=data", handler.Bodies[0].Replace("\"", ""));
        Assert.Contains("filename=2.report.pdf", handler.Bodies[0].Replace("\"", ""));
    }

    [Fact]
    public async Task Upload_UsesStreamingHttpContent_WithoutByteArrayContent()
    {
        HttpContent? dataPart = null;
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(request =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            dataPart = Assert.Single(multipart);
            return FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        });
        var client = new ApiClient(
            new HttpClient(handler), new ServerOrigin("https://vault.example"));
        var upload = new StreamUploadContent(
            3, (output, ct) => output.WriteAsync(new byte[] { 1, 2, 3 }, ct).AsTask());

        await client.UploadSendFileAsync(
            FileUploadType.Direct,
            "/sends/s1/file/f1",
            "s1",
            "f1",
            "2.encrypted-name",
            upload,
            TestContext.Current.CancellationToken);

        Assert.IsType<StreamingHttpContent>(dataPart);
        Assert.IsNotType<ByteArrayContent>(dataPart);
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
    public async Task DeleteSendForReplacement_404_IsCompleted()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.NotFound, "{}"));
        var client = NewClient(handler);

        await client.DeleteSendForReplacementAsync("s1", TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
    }

    [Fact]
    public async Task DeleteSendForReplacement_4xx_IsDefinitelyNotApplied()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.Forbidden,
            """{"message":"Delete denied"}"""));
        var client = NewClient(handler);

        var error = await Assert.ThrowsAsync<SendDeleteDefinitelyNotAppliedException>(() =>
            client.DeleteSendForReplacementAsync("s1", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal("s1", error.SendId);
    }

    [Fact]
    public async Task DeleteSendForReplacement_5xx_IsUncertain()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadGateway,
            """{"message":"Upstream response lost"}"""));
        var client = NewClient(handler);

        var error = await Assert.ThrowsAsync<SendDeleteOutcomeUncertainException>(() =>
            client.DeleteSendForReplacementAsync("s1", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal("s1", error.SendId);
    }

    [Fact]
    public async Task DeleteSendForReplacement_TransportFailure_IsUncertain()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("Connection closed after request dispatch.")));
        var client = NewClient(handler);

        var error = await Assert.ThrowsAsync<SendDeleteOutcomeUncertainException>(() =>
            client.DeleteSendForReplacementAsync("s1", TestContext.Current.CancellationToken));

        Assert.Equal("s1", error.SendId);
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
    public async Task RequestSendAccessToken_PostsOfficialGrantFields()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            """{"access_token":"send-token","expires_in":300,"token_type":"Bearer","scope":"api.send.access"}"""));
        var client = NewAnonymousClient(handler);

        var result = await client.RequestSendAccessTokenAsync(
            "base64url-send-id",
            "password-proof",
            TestContext.Current.CancellationToken);

        var success = Assert.IsType<SendAccessTokenResult.Success>(result);
        Assert.Equal("send-token", success.AccessToken);
        Assert.Equal("/identity/connect/token", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("grant_type=send_access", handler.Bodies[0]);
        Assert.Contains("client_id=send", handler.Bodies[0]);
        Assert.Contains("scope=api.send.access", handler.Bodies[0]);
        Assert.Contains("send_id=base64url-send-id", handler.Bodies[0]);
        Assert.Contains("password_hash_b64=password-proof", handler.Bodies[0]);
    }

    [Theory]
    [InlineData("unsupported_grant_type", null)]
    [InlineData("", "Invalid type")]
    public async Task RequestSendAccessToken_LegacyServerResponse_IsUnsupported(
        string errorCode,
        string? message)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            $$"""{"error":"{{errorCode}}","message":{{System.Text.Json.JsonSerializer.Serialize(message)}}}"""));
        var client = NewAnonymousClient(handler);

        var result = await client.RequestSendAccessTokenAsync(
            "base64url-send-id",
            null,
            TestContext.Current.CancellationToken);

        Assert.IsType<SendAccessTokenResult.Unsupported>(result);
    }

    [Fact]
    public async Task RequestSendAccessToken_StructuredNotFound_RemainsProtocolError()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.NotFound,
            """{"error":"invalid_grant","error_description":"send_id is invalid.","send_access_error_type":"send_id_invalid"}"""));
        var client = NewAnonymousClient(handler);

        var result = await client.RequestSendAccessTokenAsync(
            "base64url-send-id",
            null,
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<SendAccessTokenResult.Error>(result);
        Assert.Equal(404, error.HttpStatus);
        Assert.Equal("send_id_invalid", error.SendAccessErrorType);
    }

    [Theory]
    [InlineData(500, "", "Invalid type", null)]
    [InlineData(400, "unsupported_grant_type", null, "send_id_invalid")]
    [InlineData(404, "unsupported_grant_type", null, null)]
    public async Task RequestSendAccessToken_NonLegacyError_RemainsProtocolError(
        int statusCode,
        string errorCode,
        string? message,
        string? sendAccessErrorType)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            (HttpStatusCode)statusCode,
            $$"""
            {
              "error": "{{errorCode}}",
              "message": {{System.Text.Json.JsonSerializer.Serialize(message)}},
              "send_access_error_type": {{System.Text.Json.JsonSerializer.Serialize(sendAccessErrorType)}}
            }
            """));
        var client = NewAnonymousClient(handler);

        var result = await client.RequestSendAccessTokenAsync(
            "base64url-send-id",
            null,
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<SendAccessTokenResult.Error>(result);
        Assert.Equal(statusCode, error.HttpStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Not Found</html>")]
    [InlineData("{\"message\":\"Not Found\"}")]
    public async Task RequestSendAccessToken_UnstructuredNotFound_RemainsProtocolError(
        string responseBody)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(responseBody),
        });
        var client = NewAnonymousClient(handler);

        var result = await client.RequestSendAccessTokenAsync(
            "base64url-send-id",
            null,
            TestContext.Current.CancellationToken);

        var error = Assert.IsType<SendAccessTokenResult.Error>(result);
        Assert.Equal(404, error.HttpStatus);
    }

    [Fact]
    public async Task AccessSendWithToken_PostsBearerToOfficialAccessRoute()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"acc1","type":0,"name":"2.n","text":{"text":"2.t","hidden":false},"file":null,"expirationDate":null,"creatorIdentifier":null,"object":"send-access"}"""));
        var client = NewAnonymousClient(handler);

        await client.AccessSendWithTokenAsync(
            "send-token",
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/sends/access/", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("send-token", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task AccessSendFileWithToken_PostsBearerAndFileId()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"id":"file-1","url":"https://vault.example/download","object":"send-fileDownload"}"""));
        var client = NewAnonymousClient(handler);

        await client.AccessSendFileWithTokenAsync(
            "file-1",
            "send-token",
            TestContext.Current.CancellationToken);

        Assert.Equal("/api/sends/access/file/file-1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("send-token", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task OpenSendFileDownload_GetsStreamFromUrl()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 2, 1, 2, 3 }),
        });
        var client = NewClient(handler);

        await using var download = await client.OpenSendFileDownloadAsync(
            "https://vault.example/api/sends/s1/file-1?t=tok", TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await download.Stream.CopyToAsync(copy, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal("/api/sends/s1/file-1", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(new byte[] { 2, 1, 2, 3 }, copy.ToArray());
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
