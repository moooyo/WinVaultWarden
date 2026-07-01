using App.ViewModels.Models;

namespace App.Services;

public enum VaultSortKey { NameAsc, NameDesc, RevisionDesc, CreationDesc }

public sealed record VaultFacets(bool Totp, bool Attachment, bool Uri, bool FavoriteOnly)
{
    public static readonly VaultFacets None = new(false, false, false, false);
    public bool Any => Totp || Attachment || Uri || FavoriteOnly;
}

public static class VaultQuery
{
    public static IReadOnlyList<CipherListItem> Apply(
        IEnumerable<CipherListItem> baseItems, string? search, VaultFacets facets, VaultSortKey sort)
    {
        IEnumerable<CipherListItem> q = baseItems;

        if (facets.Totp) q = q.Where(i => i.HasTotp);
        if (facets.Attachment) q = q.Where(i => i.HasAttachment);
        if (facets.Uri) q = q.Where(i => i.HasUri);
        if (facets.FavoriteOnly) q = q.Where(i => i.Favorite);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLowerInvariant();
            q = q.Where(i => i.SearchHaystack.Contains(needle, StringComparison.Ordinal));
        }

        return sort switch
        {
            VaultSortKey.NameAsc      => q.OrderBy(i => i.Name, StringComparer.CurrentCulture).ThenBy(i => i.Id, StringComparer.Ordinal).ToList(),
            VaultSortKey.NameDesc     => q.OrderByDescending(i => i.Name, StringComparer.CurrentCulture).ThenBy(i => i.Id, StringComparer.Ordinal).ToList(),
            VaultSortKey.RevisionDesc => q.OrderByDescending(i => i.RevisionDate).ThenBy(i => i.Id, StringComparer.Ordinal).ToList(),
            VaultSortKey.CreationDesc => q.OrderByDescending(i => i.CreationDate).ThenBy(i => i.Id, StringComparer.Ordinal).ToList(),
            _ => q.ToList(),
        };
    }
}
