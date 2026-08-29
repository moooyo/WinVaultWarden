using System.Globalization;
using System.Net;
using Api;
using Core.Enums;
using Core.Models;
using Core.Session;
using Xunit;

namespace Vault.Tests;

public sealed class AzureFileUploadApiClientTests
{
    private const string FirstUploadUrl =
        "https://vault.example/api/blob/file-1?sv=2024-11-04&sig=first";
    private const string RenewedUploadUrl =
        "https://vault.example/api/blob/file-1?sv=2025-05-05&sig=renewed";

    private static ApiClient NewClient(FakeHttpMessageHandler handler)
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var auth = new AuthHeaderHandler(
            endpoints,
            () => new AccessTokenBinding("account", endpoints, 1, "account-token"),
            (_, _) => Task.FromResult(false))
        {
            InnerHandler = handler,
        };
        return new ApiClient(new HttpClient(auth), endpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureUpload_PutsRawBodyWithBlobHeadersAndNoBearer(bool attachment)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = NewClient(handler);
        var writes = 0;
        var upload = Content("encrypted-body", () => writes++);

        if (attachment)
        {
            await client.UploadAttachmentDataAsync(
                FileUploadType.Azure,
                FirstUploadUrl,
                "cipher-1",
                "attachment-1",
                "2.encrypted-name",
                upload,
                TestContext.Current.CancellationToken);
        }
        else
        {
            await client.UploadSendFileAsync(
                FileUploadType.Azure,
                FirstUploadUrl,
                "send-1",
                "file-1",
                "2.encrypted-name",
                upload,
                TestContext.Current.CancellationToken);
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/api/blob/file-1", request.RequestUri!.AbsolutePath);
        Assert.Equal("sv=2024-11-04&sig=first", request.RequestUri.Query.TrimStart('?'));
        Assert.IsType<StreamingHttpContent>(request.Content);
        Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("encrypted-body".Length, request.Content.Headers.ContentLength);
        Assert.Equal("BlockBlob", Assert.Single(request.Headers.GetValues("x-ms-blob-type")));
        var azureDate = Assert.Single(request.Headers.GetValues("x-ms-date"));
        Assert.True(DateTimeOffset.TryParseExact(
            azureDate,
            "R",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _));
        Assert.Equal("2024-11-04", Assert.Single(request.Headers.GetValues("x-ms-version")));
        Assert.Null(request.Headers.Authorization);
        Assert.True(request.Options.TryGetValue(
            AuthHeaderHandler.SuppressBearerAuthorizationKey,
            out var suppressBearer) && suppressBearer);
        Assert.True(request.Options.TryGetValue(
            AuthHeaderHandler.DisableAutomaticRetryKey,
            out var disableRetry) && disableRetry);
        Assert.True(request.Options.TryGetValue(
            RequestDeadlineHandler.UseStreamingUploadIdleTimeoutKey,
            out var streamingTimeout) && streamingTimeout);
        Assert.Equal("encrypted-body", handler.Bodies[0]);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData("/api/blob/file-1?sv=2024-11-04")]
    [InlineData("http://vault.example/api/blob/file-1?sv=2024-11-04")]
    [InlineData("https://user@vault.example/api/blob/file-1?sv=2024-11-04")]
    [InlineData("https://@vault.example/api/blob/file-1?sv=2024-11-04")]
    [InlineData("https://vault.example/api/blob/file-1?sv=2024-11-04#fragment")]
    [InlineData("https://vault.example/api/blob/file-1?sig=missing-version")]
    [InlineData("https://vault.example/api/blob/file-1?sv=&sig=empty-version")]
    [InlineData("https://vault.example/api/blob/file-1?sv=2024-11-04&sv=2025-05-05")]
    [InlineData("https://vault.example/api/blob/file-1?sv=2024-11-04&SV=2025-05-05")]
    public async Task AzureUpload_InvalidSasUrl_FailsBeforeDispatchOrBodyRead(string uploadUrl)
    {
        var handler = new FakeHttpMessageHandler();
        using var client = NewClient(handler);
        var writes = 0;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.UploadSendFileAsync(
                FileUploadType.Azure,
                uploadUrl,
                "send-1",
                "file-1",
                "2.name",
                Content("body", () => writes++),
                TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task UnknownUploadType_FailsClosedBeforeDispatchOrBodyRead()
    {
        var handler = new FakeHttpMessageHandler();
        using var client = NewClient(handler);
        var writes = 0;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.UploadSendFileAsync(
                (FileUploadType)42,
                FirstUploadUrl,
                "send-1",
                "file-1",
                "2.name",
                Content("body", () => writes++),
                TestContext.Current.CancellationToken));

        Assert.Contains("42", error.Message);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task AzureSendUpload_FirstHttpFailure_RenewsAndRetriesExactlyOnce()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.Forbidden,
            "{\"message\":\"SAS expired\"}"));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"url":"{{RenewedUploadUrl}}","fileUploadType":1}"""));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = NewClient(handler);
        var writes = 0;

        await client.UploadSendFileAsync(
            FileUploadType.Azure,
            FirstUploadUrl,
            "send-1",
            "file-1",
            "2.name",
            Content("body", () => writes++),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[0].Method);
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal("/api/sends/send-1/file/file-1", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("account-token", handler.Requests[1].Headers.Authorization!.Parameter);
        Assert.Equal(HttpMethod.Put, handler.Requests[2].Method);
        Assert.Equal(new Uri(RenewedUploadUrl), handler.Requests[2].RequestUri);
        Assert.Null(handler.Requests[2].Headers.Authorization);
        Assert.Equal("2025-05-05", Assert.Single(handler.Requests[2].Headers.GetValues("x-ms-version")));
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task AzureAttachmentUpload_FirstHttpFailure_UsesAttachmentRenewalRoute()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"url":"{{RenewedUploadUrl}}","fileUploadType":1}"""));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = NewClient(handler);

        await client.UploadAttachmentDataAsync(
            FileUploadType.Azure,
            FirstUploadUrl,
            "cipher-1",
            "attachment-1",
            "2.name",
            Content("body"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(
            "/api/ciphers/cipher-1/attachment/attachment-1/renew",
            handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("account-token", handler.Requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task AzureUpload_SecondHttpFailure_DoesNotRenewAgain()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"url":"{{RenewedUploadUrl}}","fileUploadType":1}"""));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = NewClient(handler);

        await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.UploadSendFileAsync(
                FileUploadType.Azure,
                FirstUploadUrl,
                "send-1",
                "file-1",
                "2.name",
                Content("body"),
                TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Get));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task AzureUpload_UnexpectedSuccessStatus_IsNotAccepted(
        HttpStatusCode statusCode)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(statusCode));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            $$"""{"url":"{{RenewedUploadUrl}}","fileUploadType":1}"""));
        handler.Enqueue(_ => new HttpResponseMessage(statusCode));
        using var client = NewClient(handler);

        await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.UploadSendFileAsync(
                FileUploadType.Azure,
                FirstUploadUrl,
                "send-1",
                "file-1",
                "2.encrypted-name",
                Content("encrypted-body"),
                TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
    }

    [Fact]
    public async Task AzureUpload_CallerCancellation_DoesNotRenew()
    {
        using var caller = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue((_, _) =>
        {
            caller.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(caller.Token);
        });
        using var client = NewClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.UploadSendFileAsync(
                FileUploadType.Azure,
                FirstUploadUrl,
                "send-1",
                "file-1",
                "2.name",
                Content("body"),
                caller.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AzureUpload_SourceConsistencyFailure_DoesNotRenew()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = NewClient(handler);
        var upload = new StreamUploadContent(
            4,
            (_, _) => Task.FromException(
                new HttpRequestException(
                    "Upload body failed.",
                    new InvalidDataException("File content changed between encryption passes."))));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UploadSendFileAsync(
                FileUploadType.Azure,
                FirstUploadUrl,
                "send-1",
                "file-1",
                "2.name",
                upload,
                TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AzureUpload_RenewalChangesTransport_FailsBeforeRetry()
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"url\":\"/sends/send-1/file/file-1\",\"fileUploadType\":0}"));
        using var client = NewClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.UploadSendFileAsync(
                FileUploadType.Azure,
                FirstUploadUrl,
                "send-1",
                "file-1",
                "2.name",
                Content("body"),
                TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Requests.Count);
    }

    private static StreamUploadContent Content(string value, Action? onWrite = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return new StreamUploadContent(
            bytes.LongLength,
            async (destination, ct) =>
            {
                onWrite?.Invoke();
                await destination.WriteAsync(bytes, ct);
            });
    }
}
