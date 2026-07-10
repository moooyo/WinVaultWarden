using Core.Enums;

namespace Core.Models;

// Send 写入草稿。Id 为 null 表示创建,非 null 表示更新。
// PasswordChange 是显式三态(Keep/Set/Remove),不得用 null/空字符串暗号代替。
public sealed class SendDraftModel
{
    public string? Id { get; init; }
    // 编辑既有文本/文件 Send 时,原 wrapped seed(akey)。写入层复用它解出的 seed 以保持服务端数据可解密,
    // 绝不为更新生成新 seed —— 否则服务端仍以旧 seed 加密的字段(文件名/文件字节/文本)将永久无法解密。
    public string? Key { get; init; }
    public SendType Type { get; init; }
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public string? TextContent { get; init; }
    public bool TextHidden { get; init; }
    public string? FileName { get; init; }
    public int? MaxAccessCount { get; init; }
    public DateTimeOffset? ExpirationDate { get; init; }
    public DateTimeOffset DeletionDate { get; init; }
    public bool Disabled { get; init; }
    public bool HideEmail { get; init; }
    public SendPasswordChange PasswordChange { get; init; } = new SendPasswordChange.Keep();
}
