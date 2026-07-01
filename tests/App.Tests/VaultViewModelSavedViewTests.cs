using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class VaultViewModelSavedViewTests
{
    private sealed class InMemorySavedSearchStore : ISavedSearchStore
    {
        private readonly List<SavedSearchView> _views = new();

        public IReadOnlyList<SavedSearchView> GetAll() => _views;

        public void Save(SavedSearchView view)
        {
            _views.RemoveAll(v => v.Name == view.Name);
            _views.Add(view);
        }

        public void Delete(string name) => _views.RemoveAll(v => v.Name == name);
    }

    [Fact]
    public void SaveCurrentView_PersistsCapturedStateAndAddsToSavedViews()
    {
        var store = new InMemorySavedSearchStore();
        var vm = new VaultViewModel(new MockVaultUiService(), savedStore: store);

        vm.FacetTotp = true;
        vm.SearchText = "x";
        vm.SelectedSort = VaultSortKey.CreationDesc;

        vm.SaveCurrentViewCommand.Execute("V1");

        var saved = Assert.Single(store.GetAll());
        Assert.Equal("V1", saved.Name);
        Assert.True(saved.Facets.Totp);
        Assert.Equal("x", saved.Search);
        Assert.Equal(VaultSortKey.CreationDesc, saved.Sort);
        Assert.Contains(vm.SavedViews, v => v.Name == "V1");
    }

    [Fact]
    public void ApplyView_RestoresCapturedState()
    {
        var store = new InMemorySavedSearchStore();
        var vm = new VaultViewModel(new MockVaultUiService(), savedStore: store);

        vm.FacetTotp = true;
        vm.SearchText = "x";
        vm.SelectedSort = VaultSortKey.CreationDesc;
        vm.SaveCurrentViewCommand.Execute("V1");
        var view = store.GetAll().Single(v => v.Name == "V1");

        // 改变状态
        vm.FacetTotp = false;
        vm.SearchText = "changed";
        vm.SelectedSort = VaultSortKey.NameAsc;

        vm.ApplyViewCommand.Execute(view);

        Assert.True(vm.FacetTotp);
        Assert.Equal("x", vm.SearchText);
        Assert.Equal(VaultSortKey.CreationDesc, vm.SelectedSort);
    }

    [Fact]
    public void DeleteView_RemovesFromStoreAndSavedViews()
    {
        var store = new InMemorySavedSearchStore();
        var vm = new VaultViewModel(new MockVaultUiService(), savedStore: store);

        vm.SearchText = "x";
        vm.SaveCurrentViewCommand.Execute("V1");

        vm.DeleteViewCommand.Execute("V1");

        Assert.Empty(store.GetAll());
        Assert.Empty(vm.SavedViews);
    }

    [Fact]
    public void SaveCurrentView_SameNameTwice_OverwritesWithoutDuplicate()
    {
        var store = new InMemorySavedSearchStore();
        var vm = new VaultViewModel(new MockVaultUiService(), savedStore: store);

        vm.SearchText = "first";
        vm.SaveCurrentViewCommand.Execute("V1");

        vm.SearchText = "second";
        vm.SaveCurrentViewCommand.Execute("V1");

        Assert.Single(store.GetAll());
        Assert.Single(vm.SavedViews);
        Assert.Equal("second", store.GetAll().Single().Search);
        Assert.Equal("second", vm.SavedViews.Single().Search);
    }
}
