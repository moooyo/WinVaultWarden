namespace Api;

/// <summary>
/// Applies a total deadline to control-plane requests. Streaming data uploads opt out and use
/// a sliding write-progress deadline through the final response headers. Successful streaming
/// downloads leave this handler after headers; non-success diagnostic bodies retain the deadline.
/// </summary>
public sealed class RequestDeadlineHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> DisableRequestDeadlineKey =
        new("WinVaultWarden.DisableRequestDeadline");
    public static readonly HttpRequestOptionsKey<bool> CompleteDeadlineOnHeadersKey =
        new("WinVaultWarden.CompleteDeadlineOnHeaders");
    public static readonly HttpRequestOptionsKey<bool> UseStreamingUploadIdleTimeoutKey =
        new("WinVaultWarden.UseStreamingUploadIdleTimeout");

    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(100);
    public static readonly TimeSpan DefaultStreamingIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _deadline;
    private readonly TimeSpan _streamingIdleTimeout;

    public RequestDeadlineHandler(
        TimeSpan? deadline = null,
        TimeSpan? streamingIdleTimeout = null)
    {
        _deadline = deadline ?? DefaultDeadline;
        if (_deadline != Timeout.InfiniteTimeSpan && _deadline <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(deadline));

        _streamingIdleTimeout = streamingIdleTimeout ?? DefaultStreamingIdleTimeout;
        if (_streamingIdleTimeout != Timeout.InfiniteTimeSpan
            && _streamingIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(streamingIdleTimeout));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        if (request.Options.TryGetValue(UseStreamingUploadIdleTimeoutKey, out var streamingUpload)
            && streamingUpload)
        {
            return await SendStreamingUploadAsync(request, ct).ConfigureAwait(false);
        }

        if (_deadline == Timeout.InfiniteTimeSpan
            || request.Options.TryGetValue(DisableRequestDeadlineKey, out var disabled) && disabled)
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }

        CancellationTokenSource? deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_deadline);
        try
        {
            var response = await base.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var headersOnly = request.Options.TryGetValue(
                CompleteDeadlineOnHeadersKey,
                out var completeOnHeaders) && completeOnHeaders;
            if (headersOnly && response.IsSuccessStatusCode)
            {
                deadline.Dispose();
                deadline = null;
                return response;
            }

            response.Content = new DeadlineHttpContent(
                response.Content,
                deadline,
                ct,
                _deadline,
                headersOnly
                    ? RequestTimeoutPhase.DownloadErrorResponseBody
                    : RequestTimeoutPhase.ControlPlane);
            deadline = null;
            return response;
        }
        catch (OperationCanceledException exception)
            when (!ct.IsCancellationRequested && deadline is { IsCancellationRequested: true })
        {
            var phase = request.Options.TryGetValue(
                CompleteDeadlineOnHeadersKey,
                out var completeOnHeaders) && completeOnHeaders
                    ? RequestTimeoutPhase.DownloadResponseHeaders
                    : RequestTimeoutPhase.ControlPlane;
            throw CreateTimeoutException(_deadline, phase, exception, deadline.Token);
        }
        finally
        {
            deadline?.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendStreamingUploadAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        if (_streamingIdleTimeout == Timeout.InfiniteTimeSpan)
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        if (request.Content is null)
            throw new InvalidOperationException("Streaming uploads require request content.");

        using var idle = new UploadIdleDeadline(_streamingIdleTimeout);
        request.Content = new UploadIdleHttpContent(request.Content, idle, ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        idle.Start();
        try
        {
            var response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
            idle.Complete();
            if (!response.IsSuccessStatusCode)
            {
                response.Content = new IdleHttpContent(
                    response.Content,
                    _streamingIdleTimeout,
                    ct,
                    RequestTimeoutPhase.UploadErrorResponseBody);
            }
            return response;
        }
        catch (OperationCanceledException exception)
            when (!ct.IsCancellationRequested && idle.HasTimedOut)
        {
            throw CreateTimeoutException(
                _streamingIdleTimeout,
                idle.TimedOutPhase,
                exception,
                idle.Token);
        }
    }

    private static TaskCanceledException CreateTimeoutException(
        TimeSpan deadline,
        RequestTimeoutPhase phase,
        Exception inner,
        CancellationToken token) =>
        new(
            $"The request timed out during {phase} after {deadline}.",
            new RequestTimeoutException(phase, deadline, inner),
            token);

    private sealed class DeadlineHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationToken _callerToken;
        private readonly TimeSpan _timeout;
        private readonly RequestTimeoutPhase _phase;
        private readonly long? _contentLength;

        public DeadlineHttpContent(
            HttpContent inner,
            CancellationTokenSource deadline,
            CancellationToken callerToken,
            TimeSpan timeout,
            RequestTimeoutPhase phase)
        {
            _inner = inner;
            _deadline = deadline;
            _callerToken = callerToken;
            _timeout = timeout;
            _phase = phase;
            _contentLength = inner.Headers.ContentLength;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context,
            CancellationToken ct) =>
            RunAsync(token => _inner.CopyToAsync(stream, token), ct);

        protected override bool TryComputeLength(out long length)
        {
            if (_contentLength is long contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
        {
            var stream = await RunAsync(
                token => _inner.ReadAsStreamAsync(token),
                ct).ConfigureAwait(false);
            return new DeadlineReadStream(
                stream,
                _deadline.Token,
                _callerToken,
                _timeout,
                _phase);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _deadline.Dispose();
            }
            base.Dispose(disposing);
        }

        private async Task RunAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken ct)
        {
            await RunAsync(
                async token =>
                {
                    await operation(token).ConfigureAwait(false);
                    return true;
                },
                ct).ConfigureAwait(false);
        }

        private async Task<T> RunAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _deadline.Token);
            try
            {
                return await operation(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!ct.IsCancellationRequested
                    && !_callerToken.IsCancellationRequested
                    && _deadline.IsCancellationRequested)
            {
                throw CreateTimeoutException(
                    _timeout,
                    _phase,
                    exception,
                    _deadline.Token);
            }
        }
    }

    private sealed class DeadlineReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _deadline;
        private readonly CancellationToken _callerToken;
        private readonly TimeSpan _timeout;
        private readonly RequestTimeoutPhase _phase;

        public DeadlineReadStream(
            Stream inner,
            CancellationToken deadline,
            CancellationToken callerToken,
            TimeSpan timeout,
            RequestTimeoutPhase phase)
        {
            _inner = inner;
            _deadline = deadline;
            _callerToken = callerToken;
            _timeout = timeout;
            _phase = phase;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct) =>
            ReadArrayAsync(buffer, offset, count, ct);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken ct = default) =>
            ReadMemoryAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private Task<int> ReadArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct) =>
            RunAsync(token => _inner.ReadAsync(buffer, offset, count, token), ct);

        private async ValueTask<int> ReadMemoryAsync(Memory<byte> buffer, CancellationToken ct) =>
            await RunAsync(
                token => _inner.ReadAsync(buffer, token).AsTask(),
                ct).ConfigureAwait(false);

        private async Task<T> RunAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _deadline);
            try
            {
                return await operation(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!ct.IsCancellationRequested
                    && !_callerToken.IsCancellationRequested
                    && _deadline.IsCancellationRequested)
            {
                throw CreateTimeoutException(
                    _timeout,
                    _phase,
                    exception,
                    _deadline);
            }
        }
    }

    private sealed class UploadIdleDeadline : IDisposable
    {
        private readonly object _gate = new();
        private readonly TimeSpan _timeout;
        private readonly CancellationTokenSource _deadline = new();
        private readonly CancellationTokenRegistration _timeoutRegistration;
        private RequestTimeoutPhase _phase = RequestTimeoutPhase.UploadRequestBody;
        private RequestTimeoutPhase _timedOutPhase = RequestTimeoutPhase.UploadRequestBody;
        private bool _completed;
        private bool _hasTimedOut;

        public UploadIdleDeadline(TimeSpan timeout)
        {
            _timeout = timeout;
            _timeoutRegistration = _deadline.Token.Register(CaptureTimeoutPhase);
        }

        public CancellationToken Token => _deadline.Token;

        public bool HasTimedOut
        {
            get => _deadline.IsCancellationRequested;
        }

        public RequestTimeoutPhase TimedOutPhase
        {
            get
            {
                lock (_gate)
                    return _hasTimedOut ? _timedOutPhase : _phase;
            }
        }

        public void Start() => Touch(RequestTimeoutPhase.UploadRequestBody);

        public void ReportWriteProgress() => Touch(RequestTimeoutPhase.UploadRequestBody);

        public void AwaitResponseHeaders() => Touch(RequestTimeoutPhase.UploadResponseHeaders);

        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            _timeoutRegistration.Dispose();
            _deadline.Dispose();
        }

        private void Touch(RequestTimeoutPhase phase)
        {
            lock (_gate)
            {
                if (_completed || _hasTimedOut)
                    return;

                _phase = phase;
                _deadline.CancelAfter(_timeout);
            }
        }

        private void CaptureTimeoutPhase()
        {
            lock (_gate)
            {
                if (_completed)
                    return;

                _hasTimedOut = true;
                _timedOutPhase = _phase;
            }
        }
    }

    private sealed class UploadIdleHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly UploadIdleDeadline _idle;
        private readonly CancellationToken _callerToken;
        private readonly long? _contentLength;

        public UploadIdleHttpContent(
            HttpContent inner,
            UploadIdleDeadline idle,
            CancellationToken callerToken)
        {
            _inner = inner;
            _idle = idle;
            _callerToken = callerToken;
            _contentLength = inner.Headers.ContentLength;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context,
            CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct,
                _callerToken,
                _idle.Token);
            var progress = new UploadProgressStream(stream, _idle);
            await _inner.CopyToAsync(progress, linked.Token).ConfigureAwait(false);
            _idle.AwaitResponseHeaders();
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_contentLength is long contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class UploadProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly UploadIdleDeadline _idle;

        public UploadProgressStream(Stream inner, UploadIdleDeadline idle)
        {
            _inner = inner;
            _idle = idle;
        }

        public override bool CanRead => false;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush()
        {
            _inner.Flush();
            _idle.ReportWriteProgress();
        }

        public override async Task FlushAsync(CancellationToken ct)
        {
            await _inner.FlushAsync(ct).ConfigureAwait(false);
            _idle.ReportWriteProgress();
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _idle.ReportWriteProgress();
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct)
        {
            await _inner.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);
            _idle.ReportWriteProgress();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken ct = default)
        {
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            _idle.ReportWriteProgress();
        }

        protected override void Dispose(bool disposing)
        {
            // The transport owns the destination stream.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly TimeSpan _idleTimeout;
        private readonly CancellationToken _callerToken;
        private readonly RequestTimeoutPhase _phase;
        private readonly long? _contentLength;

        public IdleHttpContent(
            HttpContent inner,
            TimeSpan idleTimeout,
            CancellationToken callerToken,
            RequestTimeoutPhase phase)
        {
            _inner = inner;
            _idleTimeout = idleTimeout;
            _callerToken = callerToken;
            _phase = phase;
            _contentLength = inner.Headers.ContentLength;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context,
            CancellationToken ct)
        {
            await RunAsync(
                token => _inner.CopyToAsync(stream, token),
                ct).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_contentLength is long contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
        {
            var stream = new MemoryStream();
            try
            {
                await SerializeToStreamAsync(stream, null, ct).ConfigureAwait(false);
                stream.Position = 0;
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        private async Task RunAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _callerToken);
            deadline.CancelAfter(_idleTimeout);
            try
            {
                await operation(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!ct.IsCancellationRequested
                    && !_callerToken.IsCancellationRequested
                    && deadline.IsCancellationRequested)
            {
                throw CreateTimeoutException(
                    _idleTimeout,
                    _phase,
                    exception,
                    deadline.Token);
            }
        }
    }
}
