namespace App.Services;

public sealed record SavedSearchView(string Name, string? NavTag, VaultFacets Facets, string Search, VaultSortKey Sort);

// 可序列化 POCO(扁平 bool/int/string),经 AppJsonContext 落盘。
public sealed class SavedSearchViewData
{
    public string Name { get; set; } = string.Empty;
    public string? NavTag { get; set; }
    public bool FacetTotp { get; set; }
    public bool FacetAttachment { get; set; }
    public bool FacetUri { get; set; }
    public bool FacetFavoriteOnly { get; set; }
    public string Search { get; set; } = string.Empty;
    public int Sort { get; set; }   // (int)VaultSortKey

    public static SavedSearchViewData FromView(SavedSearchView v) => new()
    {
        Name = v.Name, NavTag = v.NavTag,
        FacetTotp = v.Facets.Totp, FacetAttachment = v.Facets.Attachment,
        FacetUri = v.Facets.Uri, FacetFavoriteOnly = v.Facets.FavoriteOnly,
        Search = v.Search, Sort = (int)v.Sort,
    };

    public SavedSearchView ToView() => new(
        Name, NavTag,
        new VaultFacets(FacetTotp, FacetAttachment, FacetUri, FacetFavoriteOnly),
        Search, (VaultSortKey)Sort);
}

public static class SavedSearchOps
{
    public static List<SavedSearchViewData> Upsert(List<SavedSearchViewData> list, SavedSearchViewData item)
    {
        list.RemoveAll(x => string.Equals(x.Name, item.Name, StringComparison.Ordinal));
        list.Add(item);
        return list;
    }

    public static List<SavedSearchViewData> Remove(List<SavedSearchViewData> list, string name)
    {
        list.RemoveAll(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        return list;
    }
}
