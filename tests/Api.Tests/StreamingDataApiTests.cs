using System.Net;
using System.Text;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Session;
using Xunit;

namespace Api.Tests;

public sealed class StreamingDataApiTests
{
    [Theory]
    [InlineData(false, "/api/sends/s1/file/f1")]
    [InlineData(true, "/api/ciphers/c1/attachment/a1")]
    public async Task OpenDownload_ReturnsWithoutReadingBody_AndOwnsResponse(
        bool attachment,
        string expectedPath)
    {
        var stream = new TrackingStream([2, 1, 2, 3]);
        var content = new TrackingStreamContent(stream);
        content.Headers.ContentLength = 4;
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        using var client = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));
        var ct = TestContext.Current.CancellationToken;

        var download = attachment
            ? await client.OpenAttachmentDownloadAsync(expectedPath, ct)
            : await client.OpenSendFileDownloadAsync(expectedPath, ct);

        Assert.Equal(0, stream.BytesRead);
        Assert.Equal(4, download.ContentLength);
        Assert.Equal(expectedPath, handler.Requests.Single().RequestUri!.AbsolutePath);

        var buffer = new byte[4];
        Assert.Equal(4, await download.Stream.ReadAsync(buffer, ct));
        Assert.Equal(new byte[] { 2, 1, 2, 3 }, buffer);

        await download.DisposeAsync();

        Assert.True(stream.IsDisposed);
        Assert.True(content.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = download.Stream);
        await download.DisposeAsync();
    }

    [Fact]
    public async Task OpenDownload_HttpError_DisposesResponseAndStream()
    {
        var stream = new TrackingStream(
            Encoding.UTF8.GetBytes("{\"message\":\"Download denied\"}"));
        var content = new TrackingStreamContent(stream);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = content,
            ReasonPhrase = "Forbidden",
        }));
        using var client = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));
        var ct = TestContext.Current.CancellationToken;

        var error = await Assert.ThrowsAsync<VaultApiException>(() =>
            client.OpenAttachmentDownloadAsync("/ciphers/c1/attachment/a1", ct));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.True(stream.IsDisposed);
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task UploadAttachment_UsesNonReplayableStreamingMultipart()
    {
        Type? dataPartType = null;
        long? dataPartLength = null;
        string? partName = null;
        string? partFileName = null;
        var retryDisabled = false;
        var streamingIdleTimeoutEnabled = false;
        var writeCount = 0;
        var handler = new QueueHandler();
        handler.Enqueue(async (request, ct) =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            var dataPart = Assert.Single(multipart);
            dataPartType = dataPart.GetType();
            dataPartLength = dataPart.Headers.ContentLength;
            partName = dataPart.Headers.ContentDisposition!.Name;
            partFileName = dataPart.Headers.ContentDisposition.FileName;
            retryDisabled = request.Options.TryGetValue(
                AuthHeaderHandler.DisableAutomaticRetryKey,
                out var disabled) && disabled;
            streamingIdleTimeoutEnabled = request.Options.TryGetValue(
                RequestDeadlineHandler.UseStreamingUploadIdleTimeoutKey,
                out var enabled) && enabled;
            await multipart.CopyToAsync(Stream.Null, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));
        var ct = TestContext.Current.CancellationToken;
        var upload = new StreamUploadContent(
            4,
            async (destination, ct) =>
            {
                writeCount++;
                await destination.WriteAsync(new byte[] { 2, 9, 9, 9 }, ct);
            });

        await client.UploadAttachmentDataAsync(
            FileUploadType.Direct,
            "/ciphers/c1/attachment/a1",
            "c1",
            "a1",
            "2.encrypted-name",
            upload,
            ct);

        Assert.Equal(typeof(StreamingHttpContent), dataPartType);
        Assert.NotEqual(typeof(ByteArrayContent), dataPartType);
        Assert.Equal(4, dataPartLength);
        Assert.Equal("data", partName);
        Assert.Equal("2.encrypted-name", partFileName);
        Assert.True(retryDisabled);
        Assert.True(streamingIdleTimeoutEnabled);
        Assert.Equal(1, writeCount);
    }

    [Fact]
    public async Task UploadSendFile_UnauthorizedResponse_DoesNotReplayBody()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var writeCount = 0;
        var refreshCount = 0;
        var transport = new QueueHandler();
        transport.Enqueue(async (request, ct) =>
        {
            await request.Content!.CopyToAsync(Stream.Null, ct);
            return Json(HttpStatusCode.Unauthorized, "{\"message\":\"expired\"}");
        });
        var auth = new AuthHeaderHandler(
            endpoints,
            () => new AccessTokenBinding("account", endpoints, 3, "expired-token"),
            (_, _) =>
            {
                refreshCount++;
                return Task.FromResult(true);
            })
        {
            InnerHandler = transport,
        };
        var deadline = new RequestDeadlineHandler(
            streamingIdleTimeout: TimeSpan.FromSeconds(1))
        {
            InnerHandler = auth,
        };
        using var client = new ApiClient(new HttpClient(deadline), endpoints);
        var upload = new StreamUploadContent(
            3,
            async (destination, ct) =>
            {
                writeCount++;
                await destination.WriteAsync(new byte[] { 1, 2, 3 }, ct);
            });

        await Assert.ThrowsAsync<VaultWriteException>(() =>
            client.UploadSendFileAsync(
                FileUploadType.Direct,
                "/sends/s1/file/f1",
                "s1",
                "f1",
                "2.encrypted-name",
                upload,
                TestContext.Current.CancellationToken));

        Assert.Single(transport.Requests);
        Assert.Equal(1, writeCount);
        Assert.Equal(0, refreshCount);
    }

    [Fact]
    public async Task OpenDownload_OversizedErrorBody_ReadsOnlyDiagnosticLimit()
    {
        var stream = new TrackingStream(new byte[128 * 1024]);
        var content = new TrackingStreamContent(stream);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = content,
            ReasonPhrase = "Bad Gateway",
        }));
        using var client = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));

        await Assert.ThrowsAsync<VaultApiException>(() =>
            client.OpenAttachmentDownloadAsync(
                "/ciphers/c1/attachment/a1",
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.IsDisposed);
        Assert.True(content.IsDisposed);
    }

    [Fact]
    public async Task OpenDownload_UnknownLengthErrorBody_StopsAtDiagnosticLimit()
    {
        var content = new UnknownLengthContent(128 * 1024, 1024);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = content,
            ReasonPhrase = "Bad Gateway",
        }));
        using var client = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));

        await Assert.ThrowsAsync<VaultApiException>(() =>
            client.OpenAttachmentDownloadAsync(
                "/ciphers/c1/attachment/a1",
                TestContext.Current.CancellationToken));

        Assert.Equal(64 * 1024, content.BytesCopied);
    }

    [Fact]
    public async Task ControlRequest_UnknownLengthErrorBody_IsNotPrebuffered()
    {
        var content = new UnknownLengthContent(128 * 1024, 1024);
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = content,
            ReasonPhrase = "Bad Gateway",
        }));
        using var client = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));

        await Assert.ThrowsAsync<VaultApiException>(() =>
            client.GetConfigAsync(TestContext.Current.CancellationToken));

        Assert.Equal(64 * 1024, content.BytesCopied);
    }

    [Fact]
    public async Task ControlRequest_LargeSuccessfulSyncBody_IsFullyParsed()
    {
        var padding = new string('x', 128 * 1024);
        var json =
            "{\"object\":\"sync\",\"profile\":null,\"folders\":[],\"ciphers\":[]," +
            $"\"padding\":\"{padding}\"}}";
        var handler = new QueueHandler();
        handler.Enqueue((_, _) => Task.FromResult(Json(HttpStatusCode.OK, json)));
        using var client = new ApiClient(
            new HttpClient(handler),
            new ServerOrigin("https://vault.example"));

        var result = await client.GetSyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal("sync", result.Object);
        Assert.Empty(result.Folders!);
        Assert.Empty(result.Ciphers!);
    }

    [Fact]
    public async Task CreateAttachmentV2_After401Refresh_ReceiptsSuccessfulRetryBinding()
    {
        const string responseJson =
            "{\"attachmentId\":\"a1\",\"url\":\"/ciphers/c1/attachment/a1\",\"fileUploadType\":0,\"object\":\"attachment-fileUpload\",\"cipherResponse\":{\"id\":\"c1\",\"object\":\"cipher\"}}";
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example");
        var token = "old-token";
        var transport = new QueueHandler();
        transport.Enqueue((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        transport.Enqueue((_, _) => Task.FromResult(Json(HttpStatusCode.OK, responseJson)));
        var auth = new AuthHeaderHandler(
            endpoints,
            () => new AccessTokenBinding("account", endpoints, 7, token),
            (_, _) =>
            {
                token = "refreshed-token";
                return Task.FromResult(true);
            })
        {
            InnerHandler = transport,
        };
        using var client = new ApiClient(new HttpClient(auth), endpoints);
        var ct = TestContext.Current.CancellationToken;

        var receipt = await client.CreateAttachmentV2Async(
            "c1",
            new AttachmentUploadRequest("2.key", "2.name", 65),
            ct);

        Assert.Equal("a1", receipt.Value.AttachmentId);
        Assert.Equal("refreshed-token", receipt.Authorization.Token);
        Assert.Equal("old-token", transport.Requests[0].Headers.Authorization!.Parameter);
        Assert.Equal("refreshed-token", transport.Requests[1].Headers.Authorization!.Parameter);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>>
            _responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Enqueue(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            _responses.Enqueue(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests.Add(request);
            return _responses.Dequeue()(request, ct);
        }
    }

    private sealed class TrackingStreamContent(Stream stream) : StreamContent(stream)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public bool IsDisposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            BytesRead += read;
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnknownLengthContent(int totalBytes, int chunkBytes) : HttpContent
    {
        public int BytesCopied { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken ct)
        {
            var chunk = new byte[chunkBytes];
            while (BytesCopied < totalBytes)
            {
                var count = Math.Min(chunk.Length, totalBytes - BytesCopied);
                await stream.WriteAsync(chunk.AsMemory(0, count), ct);
                BytesCopied += count;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
