namespace App.ViewModels.Models;

public enum FilterKind { AllItems, Favorites, Archive, Trash, Type, Folder, Organization, Collection, Totp }

// 栏② 过滤/文件夹节点。
public sealed partial class FilterNode
{
    public required string Label { get; init; }
    public string Glyph { get; init; } = "";
    public int Count { get; init; }
    // 列表右侧计数显示:0 时留空。
    public string CountText => Count > 0 ? Count.ToString() : string.Empty;
    public FilterKind Kind { get; init; }
    // Type 节点用:对应的 VaultItemKind;Folder 节点用:FolderId。
    public VaultItemKind? TypeFilter { get; init; }
    public string? FolderId { get; init; }
    public string? OrganizationId { get; init; }
    public string? CollectionId { get; init; }
}
