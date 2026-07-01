using App.Services;
using System.Text.Json;
using Xunit;

namespace App.Tests;

public class SavedSearchStoreTests
{
    private static SavedSearchView View(string name) =>
        new(name, "vault:folder:f1", new VaultFacets(true, false, true, false), "octo", VaultSortKey.RevisionDesc);

    [Fact]
    public void FromView_ToView_RoundTrips()
    {
        var v = View("Work");
        var back = SavedSearchViewData.FromView(v).ToView();
        Assert.Equal(v, back); // record value equality (VaultFacets is a record too)
    }

    [Fact]
    public void Data_Json_RoundTrips_Via_AppJsonContext()
    {
        var d = SavedSearchViewData.FromView(View("Work"));
        var json = JsonSerializer.Serialize(d, AppJsonContext.Default.SavedSearchViewData);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.SavedSearchViewData)!;
        Assert.Equal(d.Name, back.Name);
        Assert.Equal(d.Sort, back.Sort);
        Assert.Equal(d.FacetTotp, back.FacetTotp);
    }

    [Fact]
    public void Upsert_Overwrites_SameName()
    {
        var list = new List<SavedSearchViewData>();
        list = SavedSearchOps.Upsert(list, SavedSearchViewData.FromView(View("A")));
        list = SavedSearchOps.Upsert(list, SavedSearchViewData.FromView(
            new SavedSearchView("A", null, VaultFacets.None, "changed", VaultSortKey.NameAsc)));
        Assert.Single(list);
        Assert.Equal("changed", list[0].Search);
    }

    [Fact]
    public void Remove_ByName()
    {
        var list = new List<SavedSearchViewData> { SavedSearchViewData.FromView(View("A")) };
        list = SavedSearchOps.Remove(list, "A");
        Assert.Empty(list);
    }
}
