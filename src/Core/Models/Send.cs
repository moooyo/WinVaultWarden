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
    public SendAuthType AuthType { get; init; } = SendAuthType.Unknown;
    public int RawAuthType { get; init; } = int.MinValue;
    public bool HasPassword { get; init; }
    public DateTimeOffset? RevisionDate { get; init; }
    public string? RawMutationWitness { get; init; }
    // 完整分享 URL（含 seed 片段），仅存于内存，不可落盘或发往服务端。
    public string ShareUrl { get; init; } = "";
    // 服务端存储的 wrapped seed(akey / JSON "key")。密文,仅存内存(与 ShareUrl 一样不可落盘/发日志)。
    // 编辑文件 Send 更新元数据时必须复用它包裹的 seed,以保持服务端既有文件可解密。
    public string? Key { get; init; }
}

public sealed record SendText(string? Content, bool Hidden);

public sealed record SendFile(string FileName, long Size, string? SizeName, string FileId);
