namespace App.ViewModels.Models;

public enum FilterKind { AllItems, Favorites, Trash, Type, Folder }

// 栏② 过滤/文件夹节点。
public sealed class FilterNode
{
    public required string Label { get; init; }
    public string Glyph { get; init; } = "";
    public int Count { get; init; }
    public FilterKind Kind { get; init; }
    // Type 节点用:对应的 VaultItemKind;Folder 节点用:FolderId。
    public VaultItemKind? TypeFilter { get; init; }
    public string? FolderId { get; init; }
}
