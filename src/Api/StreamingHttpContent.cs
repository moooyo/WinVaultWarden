using System.Net;
using Core.Models;

namespace Api;

// Adapts prepared upload content to HttpContent without buffering the encrypted payload.
public sealed class StreamingHttpContent : HttpContent
{
    private readonly StreamUploadContent _content;

    public StreamingHttpContent(StreamUploadContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length < 0)
            throw new ArgumentOutOfRangeException(nameof(content), "Content length cannot be negative.");

        _content = content;
        Headers.ContentLength = content.Length;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        _content.WriteAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
        _content.WriteAsync(stream, ct);

    protected override bool TryComputeLength(out long length)
    {
        length = _content.Length;
        return true;
    }
}
