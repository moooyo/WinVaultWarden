using App.ViewModels.Models;

namespace App.Services;

public static class VaultNavigationService
{
    private const string FolderGlyph = "\uE8B7";

    public static IReadOnlyList<VaultFolderNavigationItem> BuildFolderItems(IEnumerable<FilterNode> filters) =>
        filters
            .Where(f => f.Kind == FilterKind.Folder && !string.IsNullOrWhiteSpace(f.FolderId))
            .Select(f => new VaultFolderNavigationItem(
                f.Label,
                $"vault:folder:{f.FolderId}",
                f.Glyph.Length > 0 ? f.Glyph : FolderGlyph))
            .ToList();
}
