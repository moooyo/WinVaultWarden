using App.ViewModels.Models;

namespace App.Services;

/// <summary>
/// 从过滤器节点构建文件夹导航项 DTO(<see cref="VaultFolderNavigationItem"/>)。
/// 纯构建逻辑、不执行导航;实际页面导航由 <see cref="INavigationService"/> 负责。
/// </summary>
public static class VaultNavItemBuilder
{
    private const string FolderGlyph = "\uE8B7";
    private const string OrganizationGlyph = "\uE716";
    private const string CollectionGlyph = "\uE8B7";

    public static IReadOnlyList<VaultFolderNavigationItem> BuildFolderItems(IEnumerable<FilterNode> filters) =>
        filters
            .Where(f => f.Kind == FilterKind.Folder && !string.IsNullOrWhiteSpace(f.FolderId))
            .Select(f => new VaultFolderNavigationItem(
                f.Label,
                $"vault:folder:{f.FolderId}",
                f.Glyph.Length > 0 ? f.Glyph : FolderGlyph))
            .ToList();

    public static IReadOnlyList<VaultFolderNavigationItem> BuildOrganizationItems(
        IEnumerable<FilterNode> filters) =>
        filters
            .Where(filter => filter.Kind == FilterKind.Organization
                && !string.IsNullOrWhiteSpace(filter.OrganizationId))
            .Select(filter => new VaultFolderNavigationItem(
                filter.Label,
                $"vault:organization:{filter.OrganizationId}",
                filter.Glyph.Length > 0 ? filter.Glyph : OrganizationGlyph))
            .ToList();

    public static IReadOnlyList<VaultFolderNavigationItem> BuildCollectionItems(
        IEnumerable<FilterNode> filters) =>
        filters
            .Where(filter => filter.Kind == FilterKind.Collection
                && !string.IsNullOrWhiteSpace(filter.CollectionId))
            .Select(filter => new VaultFolderNavigationItem(
                filter.Label,
                $"vault:collection:{filter.CollectionId}",
                filter.Glyph.Length > 0 ? filter.Glyph : CollectionGlyph))
            .ToList();
}
