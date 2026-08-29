namespace Api;

/// <summary>
/// Owns a successful HTTP download response and its content stream. Disposing this object closes
/// both resources; callers must keep it alive for the entire read operation.
/// </summary>
public sealed class DownloadContent : IAsyncDisposable
{
    private HttpResponseMessage? _response;
    private readonly Stream _stream;
    private int _disposed;

    internal DownloadContent(HttpResponseMessage response, Stream stream)
    {
        _response = response ?? throw new ArgumentNullException(nameof(response));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ContentLength = response.Content.Headers.ContentLength;
    }

    /// <summary>
    /// Creates stream-owned download content for custom API implementations.
    /// </summary>
    public DownloadContent(Stream stream, long? contentLength = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (contentLength < 0)
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        ContentLength = contentLength;
    }

    public Stream Stream
    {
        get
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            return _stream;
        }
    }

    public long? ContentLength { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var response = Interlocked.Exchange(ref _response, null);
        if (response is not null)
        {
            response.Dispose();
            return;
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
