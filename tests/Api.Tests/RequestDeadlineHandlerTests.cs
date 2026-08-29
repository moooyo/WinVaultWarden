using System.Net;
using Api;
using Xunit;

namespace Api.Tests;

public sealed class RequestDeadlineHandlerTests
{
    [Fact]
    public async Task ControlRequest_HeaderStall_PreservesHttpTimeoutExceptionShape()
    {
        using var client = CreateClient(
            new DelegateHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetAsync("https://vault.example/api/config", TestContext.Current.CancellationToken));

        AssertTimeout(error, RequestTimeoutPhase.ControlPlane);
        Assert.False(TestContext.Current.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task ControlRequest_ResponseBodyStall_IsCoveredByTotalDeadline()
    {
        using var client = CreateClient(
            new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new BlockingContent(),
            })),
            TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.GetAsync("https://vault.example/api/config", TestContext.Current.CancellationToken));

        AssertTimeout(error, RequestTimeoutPhase.ControlPlane);
    }

    [Fact]
    public async Task DataUpload_BypassCanRunLongerThanControlDeadline()
    {
        using var client = CreateClient(
            new DelegateHandler(async (_, ct) =>
            {
                await Task.Delay(90, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            TimeSpan.FromMilliseconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://vault.example/api/upload");
        request.Options.Set(RequestDeadlineHandler.DisableRequestDeadlineKey, true);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StreamingUpload_WriteProgressCanContinuePastControlDeadline()
    {
        using var client = CreateClient(
            new DelegateHandler(async (request, ct) =>
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            deadline: TimeSpan.FromMilliseconds(25),
            streamingIdleTimeout: TimeSpan.FromMilliseconds(100));
        using var request = StreamingUploadRequest(
            new ProgressContent(30, TimeSpan.FromMilliseconds(10)));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StreamingUpload_RequestBodyStall_ReportsBodyPhase()
    {
        using var client = CreateClient(
            new DelegateHandler(async (request, ct) =>
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            deadline: TimeSpan.FromSeconds(1),
            streamingIdleTimeout: TimeSpan.FromMilliseconds(30));
        using var request = StreamingUploadRequest(new BlockingContent());

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken));

        AssertTimeout(error, RequestTimeoutPhase.UploadRequestBody);
    }

    [Fact]
    public async Task StreamingUpload_ResponseHeaderStall_ReportsHeadersPhase()
    {
        using var client = CreateClient(
            new DelegateHandler(async (request, ct) =>
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            deadline: TimeSpan.FromSeconds(1),
            streamingIdleTimeout: TimeSpan.FromMilliseconds(30));
        using var request = StreamingUploadRequest(new ByteArrayContent([1, 2, 3]));

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                TestContext.Current.CancellationToken));

        AssertTimeout(error, RequestTimeoutPhase.UploadResponseHeaders);
    }

    [Fact]
    public async Task StreamingUpload_CallerCancellation_IsNotClassifiedAsTimeout()
    {
        using var client = CreateClient(
            new DelegateHandler(async (request, ct) =>
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }),
            deadline: TimeSpan.FromSeconds(1),
            streamingIdleTimeout: TimeSpan.FromSeconds(1));
        using var request = StreamingUploadRequest(new BlockingContent());
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, caller.Token));

        Assert.True(caller.IsCancellationRequested);
        Assert.False(RequestTimeoutClassifier.IsTimeout(error, caller.Token));
    }

    [Fact]
    public async Task StreamingUpload_ErrorBodyStall_UsesStreamingIdleTimeout()
    {
        using var client = CreateClient(
            new DelegateHandler(async (request, ct) =>
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new BlockingContent(),
                };
            }),
            deadline: TimeSpan.FromSeconds(1),
            streamingIdleTimeout: TimeSpan.FromMilliseconds(30));
        using var request = StreamingUploadRequest(new ByteArrayContent([1]));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        AssertTimeout(error, RequestTimeoutPhase.UploadErrorResponseBody);
    }

    [Fact]
    public async Task DownloadBody_ProgressCanContinuePastControlDeadline()
    {
        var payload = Enumerable.Range(0, 12).Select(value => (byte)value).ToArray();
        using var client = CreateClient(
            new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowProgressStream(payload, TimeSpan.FromMilliseconds(10))),
            })),
            TimeSpan.FromMilliseconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://vault.example/api/download");
        request.Options.Set(RequestDeadlineHandler.CompleteDeadlineOnHeadersKey, true);

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);
        var result = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(payload, result);
    }

    [Fact]
    public async Task DownloadErrorBody_StaysUnderControlDeadline()
    {
        using var client = CreateClient(
            new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new BlockingContent(),
            })),
            TimeSpan.FromMilliseconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://vault.example/api/download");
        request.Options.Set(RequestDeadlineHandler.CompleteDeadlineOnHeadersKey, true);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        AssertTimeout(error, RequestTimeoutPhase.DownloadErrorResponseBody);
    }

    private static HttpClient CreateClient(
        HttpMessageHandler transport,
        TimeSpan deadline,
        TimeSpan? streamingIdleTimeout = null)
    {
        var handler = new RequestDeadlineHandler(deadline, streamingIdleTimeout)
        {
            InnerHandler = transport,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpRequestMessage StreamingUploadRequest(HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://vault.example/api/upload")
        {
            Content = content,
        };
        request.Options.Set(RequestDeadlineHandler.DisableRequestDeadlineKey, true);
        request.Options.Set(RequestDeadlineHandler.UseStreamingUploadIdleTimeoutKey, true);
        return request;
    }

    private static void AssertTimeout(
        Exception exception,
        RequestTimeoutPhase expectedPhase)
    {
        Assert.True(RequestTimeoutClassifier.IsTimeout(exception));
        var timeout = Assert.IsType<RequestTimeoutException>(exception.InnerException);
        Assert.Equal(expectedPhase, timeout.Phase);
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct) => send(request, ct);
    }

    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ProgressContent(int writeCount, TimeSpan delay) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken ct)
        {
            for (var index = 0; index < writeCount; index++)
            {
                await Task.Delay(delay, ct);
                await stream.WriteAsync(new byte[] { (byte)index }, ct);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = writeCount;
            return true;
        }
    }

    private sealed class SlowProgressStream(byte[] payload, TimeSpan delay) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.LongLength;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default)
        {
            if (_offset == payload.Length)
                return 0;
            await Task.Delay(delay, ct);
            buffer.Span[0] = payload[_offset++];
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
