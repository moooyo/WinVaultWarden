namespace App.ViewModels.Models;

public enum VaultItemKind { Login, Card, Identity, Note, Ssh }

// 栏③ 列表项。Subtitle 随类型不同(登录=用户名,网站=域名等)。
public sealed class CipherListItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public VaultItemKind Kind { get; init; }
    public bool Favorite { get; init; }
    public string? FolderId { get; init; }
    // Segoe Fluent Icon glyph(类型图标)。登录类后续可换 favicon。
    public string Glyph { get; init; } = "";
    // 是否在回收站。回收站项不出现在常规过滤结果中。
    public bool IsDeleted { get; init; }
}
