using Core.Enums;

namespace Core.Models;

// 密码库条目骨架。name/notes 等为 EncString 密文,解密是客户端职责。
public sealed class Cipher
{
    public string Id { get; init; } = string.Empty;
    public CipherType Type { get; init; }
    public string? OrganizationId { get; init; }
    public string? FolderId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public bool Favorite { get; init; }
    public DateTimeOffset RevisionDate { get; init; }
}
