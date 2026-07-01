using App.Services;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class VaultQueryTests
{
    private static CipherListItem Item(string id, string name, string haystack = "",
        bool totp = false, bool att = false, bool uri = false, bool fav = false,
        DateTimeOffset rev = default, DateTimeOffset created = default) => new()
    {
        Id = id, Name = name, SearchHaystack = (haystack + " " + name).ToLowerInvariant(),
        HasTotp = totp, HasAttachment = att, HasUri = uri, Favorite = fav,
        RevisionDate = rev, CreationDate = created,
    };

    [Fact]
    public void Search_Matches_Haystack_Substring()
    {
        var items = new[] { Item("1", "GitHub", "octocat"), Item("2", "Bank", "checking") };
        var r = VaultQuery.Apply(items, "octo", VaultFacets.None, VaultSortKey.NameAsc);
        Assert.Equal("1", Assert.Single(r).Id);
    }

    [Fact]
    public void Facets_AND_Together()
    {
        var items = new[]
        {
            Item("1", "A", totp: true, att: true),
            Item("2", "B", totp: true, att: false),
        };
        var r = VaultQuery.Apply(items, null, new VaultFacets(Totp: true, Attachment: true, Uri: false, FavoriteOnly: false), VaultSortKey.NameAsc);
        Assert.Equal("1", Assert.Single(r).Id);
    }

    [Fact]
    public void FavoriteOnly_Facet()
    {
        var items = new[] { Item("1", "A", fav: true), Item("2", "B", fav: false) };
        var r = VaultQuery.Apply(items, null, VaultFacets.None with { FavoriteOnly = true }, VaultSortKey.NameAsc);
        Assert.Equal("1", Assert.Single(r).Id);
    }

    [Fact]
    public void Sort_NameAsc_And_Desc()
    {
        var items = new[] { Item("1", "Beta"), Item("2", "alpha") };
        var asc = VaultQuery.Apply(items, null, VaultFacets.None, VaultSortKey.NameAsc);
        Assert.Equal(new[] { "alpha", "Beta" }, asc.Select(i => i.Name)); // CurrentCulture: a<B
        var desc = VaultQuery.Apply(items, null, VaultFacets.None, VaultSortKey.NameDesc);
        Assert.Equal(new[] { "Beta", "alpha" }, desc.Select(i => i.Name));
    }

    [Fact]
    public void Sort_By_Dates_Descending()
    {
        var t0 = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            Item("old", "X", rev: t0, created: t0),
            Item("new", "Y", rev: t1, created: t1),
        };
        Assert.Equal("new", VaultQuery.Apply(items, null, VaultFacets.None, VaultSortKey.RevisionDesc)[0].Id);
        Assert.Equal("new", VaultQuery.Apply(items, null, VaultFacets.None, VaultSortKey.CreationDesc)[0].Id);
    }

    [Fact]
    public void Empty_Search_And_No_Facets_Returns_All()
    {
        var items = new[] { Item("1", "A"), Item("2", "B") };
        Assert.Equal(2, VaultQuery.Apply(items, "   ", VaultFacets.None, VaultSortKey.NameAsc).Count);
    }
}
