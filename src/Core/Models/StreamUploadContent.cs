namespace Core.Models;

// 已备妥的流式上传内容:Length 是加密后 EncArrayBuffer 的精确字节数(用于 Content-Length 与服务端长度校验),
// WriteAsync 在被真正上传时才把密文流写入目标流。绝不预先缓冲整份密文;可被多次调用(每次重开明文源)。
public sealed record StreamUploadContent(
    long Length,
    Func<Stream, CancellationToken, Task> WriteAsync);
