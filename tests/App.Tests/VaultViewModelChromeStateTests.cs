using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

// 保险库页“外壳状态”计算属性:右栏空状态浮层可见性 + 筛选分面计数徽标。
public class VaultViewModelChromeStateTests
{
    private static CipherListItem Item(string id, string name) => new()
    {
        Id = id,
        Name = name,
        SearchHaystack = name.ToLowerInvariant(),
        Kind = VaultItemKind.Login,
        RevisionDate = DateTimeOffset.UnixEpoch,
        CreationDate = DateTimeOffset.UnixEpoch,
    };

    private static VaultViewModel NewVm(params CipherListItem[] items)
        => new(new FakeFacetVaultUiService(items.ToList()));

    // ── ShowNoSelectionPlaceholder = Detail is null && !IsEditing ────────────

    [Fact]
    public void ShowNoSelectionPlaceholder_TrueWhenNothingSelectedAndNotEditing()
    {
        var vm = NewVm(Item("1", "Alpha"));
        Assert.True(vm.ShowNoSelectionPlaceholder);
    }

    [Fact]
    public void ShowNoSelectionPlaceholder_FalseWhenDetailSelected()
    {
        var vm = NewVm(Item("1", "Alpha"));
        vm.SelectedItem = vm.Items.First(i => i.Id == "1");
        Assert.False(vm.ShowNoSelectionPlaceholder);
    }

    [Fact]
    public void ShowNoSelectionPlaceholder_FalseWhenEditingNewItem()
    {
        var vm = NewVm(Item("1", "Alpha"));
        vm.BeginAdd(VaultItemKind.Login); // IsEditing=true, Detail=null
        Assert.False(vm.ShowNoSelectionPlaceholder);
    }

    [Fact]
    public void ShowNoSelectionPlaceholder_RaisesPropertyChanged_WhenEditingBegins()
    {
        var vm = NewVm(Item("1", "Alpha"));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.BeginAdd(VaultItemKind.Login);

        Assert.Contains(nameof(VaultViewModel.ShowNoSelectionPlaceholder), raised);
    }

    // ── ActiveFacetCount / HasActiveFacets ───────────────────────────────────

    [Fact]
    public void ActiveFacetCount_CountsEnabledFacets()
    {
        var vm = NewVm(Item("1", "Alpha"));
        Assert.Equal(0, vm.ActiveFacetCount);

        vm.FacetTotp = true;
        vm.FacetUri = true;

        Assert.Equal(2, vm.ActiveFacetCount);
    }

    [Fact]
    public void HasActiveFacets_TrueWhenAnyFacetEnabled()
    {
        var vm = NewVm(Item("1", "Alpha"));
        Assert.False(vm.HasActiveFacets);

        vm.FacetFavoriteOnly = true;

        Assert.True(vm.HasActiveFacets);
    }

    [Fact]
    public void ActiveFacetCount_RaisesPropertyChanged_WhenFacetToggled()
    {
        var vm = NewVm(Item("1", "Alpha"));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.FacetTotp = true;

        Assert.Contains(nameof(VaultViewModel.ActiveFacetCount), raised);
        Assert.Contains(nameof(VaultViewModel.HasActiveFacets), raised);
    }
}
