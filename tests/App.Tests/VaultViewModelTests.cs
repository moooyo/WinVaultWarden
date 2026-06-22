using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class MockVaultUiServiceTests
{
    [Fact]
    public void GetItems_ReturnsFiveItems_OnePerKind()
    {
        var svc = new MockVaultUiService();
        var items = svc.GetItems();

        Assert.Equal(5, items.Count);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Login);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Card);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Identity);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Note);
        Assert.Contains(items, i => i.Kind == VaultItemKind.Ssh);
    }

    [Fact]
    public void GetDetail_LoginItem_ReturnsLoginDetailWithFields()
    {
        var svc = new MockVaultUiService();
        var detail = svc.GetDetail("1");

        var login = Assert.IsType<LoginDetail>(detail);
        Assert.Equal("百度网盘", login.Name);
        Assert.Equal("admin", login.Username);
        Assert.Equal("https://www.baidu.com", login.Uri);
    }

    [Fact]
    public void GetFilters_IncludesAllItemsAndFiveTypes()
    {
        var svc = new MockVaultUiService();
        var filters = svc.GetFilters();

        Assert.Contains(filters, f => f.Kind == FilterKind.AllItems);
        Assert.Equal(5, filters.Count(f => f.Kind == FilterKind.Type));
    }
}

public class VaultViewModelTests
{
    private static VaultViewModel NewVm() => new(new MockVaultUiService());

    [Fact]
    public void Initialize_LoadsItemsAndFilters()
    {
        var vm = NewVm();
        Assert.Equal(5, vm.Items.Count);
        Assert.NotEmpty(vm.Filters);
    }

    [Fact]
    public void SelectingItem_PopulatesDetail()
    {
        var vm = NewVm();
        vm.SelectedItem = vm.Items.First(i => i.Id == "1");
        Assert.NotNull(vm.Detail);
        Assert.IsType<LoginDetail>(vm.Detail);
    }

    [Fact]
    public void ClearingSelection_ClearsDetail()
    {
        var vm = NewVm();
        vm.SelectedItem = vm.Items.First();
        vm.SelectedItem = null;
        Assert.Null(vm.Detail);
    }

    [Fact]
    public void SearchText_FiltersItemsByName()
    {
        var vm = NewVm();
        vm.SearchText = "百度";
        Assert.Single(vm.FilteredItems);
        Assert.Equal("百度网盘", vm.FilteredItems[0].Name);
    }

    [Fact]
    public void SearchText_Empty_ShowsAllItems()
    {
        var vm = NewVm();
        vm.SearchText = "百度";
        vm.SearchText = "";
        Assert.Equal(5, vm.FilteredItems.Count);
    }
}
