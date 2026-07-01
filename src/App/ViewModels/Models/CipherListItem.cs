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
    // 登录条目的网站 host(供 favicon);非登录/无 URI 为 null。
    public string? IconDomain { get; init; }
    // 是否在回收站。回收站项不出现在常规过滤结果中。
    public bool IsDeleted { get; init; }
    // 高级搜索:建项时拼好的小写可搜文本(不含密码/隐藏字段值/CVV)。
    public string SearchHaystack { get; init; } = string.Empty;
    // 分面标记(建项时算)。
    public bool HasTotp { get; init; }
    public bool HasAttachment { get; init; }
    public bool HasUri { get; init; }
    // 排序键。
    public DateTimeOffset RevisionDate { get; init; }
    public DateTimeOffset CreationDate { get; init; }
}
