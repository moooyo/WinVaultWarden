using System.Net;
using Core.Models;

namespace Api;

// 把 StreamUploadContent 适配成 HttpContent:序列化时直接调用其 WriteAsync 委托,把密文流式写进网络流,
// 绝不预先缓冲整份密文。TryComputeLength 返回精确的加密后字节数,使 Content-Length 正确、无需分块。
public sealed class StreamingHttpContent : HttpContent
{
    private readonly StreamUploadContent _content;

    public StreamingHttpContent(StreamUploadContent content) => _content = content;

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
