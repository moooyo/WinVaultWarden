using Core.Enums;

namespace Core.Models;

// Send 写入草稿。Id 为 null 表示创建,非 null 表示更新。
// Password: null = 无密码;"" = 移除已有密码;非空 = 设置/替换密码。
public sealed class SendDraftModel
{
    public string? Id { get; init; }
    // 编辑既有文件 Send 时,原 wrapped seed(akey)。写入层复用它解出的 seed 以保持服务端文件可解密。
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
    public string? Password { get; init; }
}
