using Core.Enums;

namespace Core.Models;

// 解密后的 Send 领域模型。明文字段(name/notes/text/fileName)只允许存在于内存中。
public sealed class Send
{
    public string Id { get; init; } = "";
    public string AccessId { get; init; } = "";
    public SendType Type { get; init; }
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public SendText? Text { get; init; }
    public SendFile? File { get; init; }
    public int? MaxAccessCount { get; init; }
    public int AccessCount { get; init; }
    public DateTimeOffset? ExpirationDate { get; init; }
    public DateTimeOffset DeletionDate { get; init; }
    public bool Disabled { get; init; }
    public bool HideEmail { get; init; }
    public bool HasPassword { get; init; }
    // 完整分享 URL（含 seed 片段），仅存于内存，不可落盘或发往服务端。
    public string ShareUrl { get; init; } = "";
}

public sealed record SendText(string? Content, bool Hidden);

public sealed record SendFile(string FileName, long Size, string? SizeName, string FileId);
