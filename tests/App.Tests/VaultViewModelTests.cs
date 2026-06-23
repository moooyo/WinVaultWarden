using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class MockVaultUiServiceTests
{
    [Fact]
    public void GetItems_ReturnsSixItems_IncludingTrash()
    {
        var svc = new MockVaultUiService();
        var items = svc.GetItems();

        Assert.Equal(6, items.Count);
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
        Assert.Equal(6, vm.Items.Count);
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
        // 5 = 非回收站项数（总 6 项减去 1 个 IsDeleted 项）
        Assert.Equal(5, vm.FilteredItems.Count);
    }

    [Fact]
    public void ApplyFilter_AllItems_ExcludesDeleted()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.AllItems);
        Assert.All(vm.FilteredItems, i => Assert.False(i.IsDeleted));
        Assert.Equal(vm.Items.Count(i => !i.IsDeleted), vm.FilteredItems.Count);
    }

    [Fact]
    public void ApplyFilter_Favorites_OnlyFavorite()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Favorites);
        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, i => Assert.True(i.Favorite && !i.IsDeleted));
    }

    [Fact]
    public void ApplyFilter_Trash_OnlyDeleted()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Trash);
        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, i => Assert.True(i.IsDeleted));
    }

    [Fact]
    public void ApplyFilter_ByType_OnlyThatType()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Type && f.TypeFilter == VaultItemKind.Login);
        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, i => Assert.True(i.Kind == VaultItemKind.Login && !i.IsDeleted));
    }

    [Fact]
    public void ApplyFilter_ByFolder_OnlyThatFolder()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Folder);
        var folderId = vm.SelectedFilter!.FolderId;
        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, i => Assert.True(i.FolderId == folderId && !i.IsDeleted));
    }

    [Fact]
    public void SelectFilterByTag_TypeLogin_FiltersLoginItems()
    {
        var vm = NewVm();

        vm.SelectFilterByTag("vault:type:Login");

        Assert.Equal(FilterKind.Type, vm.SelectedFilter!.Kind);
        Assert.Equal(VaultItemKind.Login, vm.SelectedFilter.TypeFilter);
        Assert.All(vm.FilteredItems, item => Assert.Equal(VaultItemKind.Login, item.Kind));
    }

    [Fact]
    public void SelectFilterByTag_Folder_FiltersFolderItems()
    {
        var vm = NewVm();

        vm.SelectFilterByTag("vault:folder:f1");

        Assert.Equal(FilterKind.Folder, vm.SelectedFilter!.Kind);
        Assert.Equal("f1", vm.SelectedFilter.FolderId);
        Assert.All(vm.FilteredItems, item => Assert.Equal("f1", item.FolderId));
    }

    [Fact]
    public void SelectFilterByTag_UnknownTag_DefaultsToAllItems()
    {
        var vm = NewVm();

        vm.SelectFilterByTag("vault:unknown");

        Assert.Equal(FilterKind.AllItems, vm.SelectedFilter!.Kind);
        Assert.All(vm.FilteredItems, item => Assert.False(item.IsDeleted));
    }

    [Fact]
    public void ApplyFilter_TypeAndSearch_Combined()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Type && f.TypeFilter == VaultItemKind.Login);
        vm.SearchText = "百度";
        Assert.All(vm.FilteredItems, i => Assert.Equal(VaultItemKind.Login, i.Kind));
        Assert.All(vm.FilteredItems, i => Assert.Contains("百度", i.Name));
    }

    [Fact]
    public void Initialize_DefaultsToAllItemsFilter()
    {
        var vm = NewVm();
        Assert.NotNull(vm.SelectedFilter);
        Assert.Equal(FilterKind.AllItems, vm.SelectedFilter!.Kind);
    }
}

public class SendViewModelTests
{
    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
    }

    [Fact]
    public void MockSendUiService_ReturnsThreeSends()
    {
        var service = new MockSendUiService();

        var sends = service.GetSends();

        Assert.Equal(3, sends.Count);
        Assert.Contains(sends, s => s.Type == SendType.Text);
        Assert.Contains(sends, s => s.Type == SendType.File);
    }

    [Fact]
    public void SelectFilterByTag_Text_ShowsOnlyTextSends()
    {
        var vm = new SendViewModel(new MockSendUiService());

        vm.SelectFilterByTag("send:text");

        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, item => Assert.Equal(SendType.Text, item.Type));
    }

    [Fact]
    public void SelectFilterByTag_File_ShowsOnlyFileSends()
    {
        var vm = new SendViewModel(new MockSendUiService());

        vm.SelectFilterByTag("send:file");

        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, item => Assert.Equal(SendType.File, item.Type));
    }

    [Fact]
    public void SelectFilterByTag_All_ShowsAllSends()
    {
        var vm = new SendViewModel(new MockSendUiService());

        vm.SelectFilterByTag("send:all");

        Assert.Equal(vm.Items.Count, vm.FilteredItems.Count);
    }

    [Fact]
    public void CopyLinkCommand_CopiesLinkWhenPresent()
    {
        var clipboard = new RecordingClipboard();
        var vm = new SendViewModel(new MockSendUiService(), clipboard);
        var item = vm.Items.First(s => !string.IsNullOrEmpty(s.Link));

        vm.CopyLinkCommand.Execute(item);

        Assert.Equal(item.Link, clipboard.Text);
    }
}
