using Core.Enums;

namespace Core.Models;

// 访问分享 Send 后的解密结果。Seed 保留以便下载文件后用同一 cryptoKey 解密。
// SendId / FileId 保留供 DownloadFileAsync 发起 access-file 中间请求换取真实下载 URL。
public sealed class SendAccessResult
{
    public SendType Type { get; init; }
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public string? TextContent { get; init; }
    public string? FileName { get; init; }
    public string? FileDownloadUrl { get; init; }
    /// <summary>文件型 Send 服务端 UUID(来自 SendAccessResponseDto.Id)。用于 access-file 中间步骤。</summary>
    public string? SendId { get; init; }
    /// <summary>文件型 Send 的 file_id(来自 SendFileDto.Id)。用于 access-file 中间步骤。</summary>
    public string? FileId { get; init; }
    public string AccessId { get; init; } = "";
    public byte[] Seed { get; init; } = Array.Empty<byte>();
    /// <summary>访问时使用的密码证明(base64)。文件下载中间步骤需重新提交。</summary>
    public string? PasswordProof { get; init; }
}
