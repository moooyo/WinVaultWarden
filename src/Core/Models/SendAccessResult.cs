using Core.Enums;

namespace Core.Models;

// 访问分享 Send 后的解密结果。Seed 保留以便下载文件后用同一 cryptoKey 解密。
public sealed class SendAccessResult
{
    public SendType Type { get; init; }
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public string? TextContent { get; init; }
    public string? FileName { get; init; }
    public string? FileDownloadUrl { get; init; }
    public string AccessId { get; init; } = "";
    public byte[] Seed { get; init; } = Array.Empty<byte>();
}
