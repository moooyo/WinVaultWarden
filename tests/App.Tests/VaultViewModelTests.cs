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

    [Fact]
    public void BuildFolderNavigationItems_ReturnsOnlyFolderFilters()
    {
        var filters = new MockVaultUiService().GetFilters();

        var folders = VaultNavigationService.BuildFolderItems(filters);

        Assert.NotEmpty(folders);
        Assert.All(folders, f => Assert.StartsWith("vault:folder:", f.Tag, StringComparison.Ordinal));
    }

    [Fact]
    public void HasFolders_ReturnsFalseWhenNoFolderFilters()
    {
        var filters = new[]
        {
            new FilterNode { Label = "所有项目", Kind = FilterKind.AllItems },
            new FilterNode { Label = "登录", Kind = FilterKind.Type, TypeFilter = VaultItemKind.Login },
        };

        var folders = VaultNavigationService.BuildFolderItems(filters);

        Assert.Empty(folders);
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

    [Fact]
    public void BeginAdd_CreatesEditorDraftAndClearsSelection()
    {
        var vm = NewVm();
        vm.SelectedItem = vm.Items.First();

        vm.BeginAdd(VaultItemKind.Card);

        Assert.True(vm.IsEditing);
        Assert.NotNull(vm.EditorDraft);
        Assert.Equal(VaultItemKind.Card, vm.EditorDraft!.Type);
        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.Detail);
        Assert.Equal("新增支付卡", vm.EditorTitle);
    }

    [Fact]
    public void CancelEdit_ClearsEditorState()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Identity);

        vm.CancelEdit();

        Assert.False(vm.IsEditing);
        Assert.Null(vm.EditorDraft);
        Assert.Equal("", vm.EditorError);
    }

    [Fact]
    public void ChangeEditorType_PreservesCommonFields()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "shared name";
        vm.EditorDraft.Notes = "shared notes";

        vm.ChangeEditorType(VaultItemKind.Ssh);

        Assert.Equal(VaultItemKind.Ssh, vm.EditorDraft!.Type);
        Assert.Equal("shared name", vm.EditorDraft.Name);
        Assert.Equal("shared notes", vm.EditorDraft.Notes);
        Assert.Equal("新增 SSH 密钥", vm.EditorTitle);
    }

    [Fact]
    public void SaveDraft_MissingName_ReturnsFalseAndKeepsEditing()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "";

        var saved = vm.SaveDraft();

        Assert.False(saved);
        Assert.True(vm.IsEditing);
        Assert.Equal("项目名称为必填项。", vm.EditorError);
    }

    [Fact]
    public void SaveDraft_Login_AddsItemSelectsItAndShowsDetail()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "GitHub";
        vm.EditorDraft.Login.Username = "octo";
        vm.EditorDraft.Login.Password = "secret";
        vm.EditorDraft.Login.Uris[0].Uri = "https://github.com";

        var saved = vm.SaveDraft();

        Assert.True(saved);
        Assert.False(vm.IsEditing);
        Assert.NotNull(vm.SelectedItem);
        Assert.Equal("GitHub", vm.SelectedItem!.Name);
        Assert.Contains(vm.Items, item => item.Name == "GitHub" && item.Kind == VaultItemKind.Login);
        var detail = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.Equal("octo", detail.Username);
        Assert.Equal("https://github.com", detail.Uri);
    }

    [Fact]
    public void SaveDraft_Ssh_RequiresKeyFields()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Ssh);
        vm.EditorDraft!.Name = "prod";
        vm.EditorDraft.SshKey.PrivateKey = "private";

        var saved = vm.SaveDraft();

        Assert.False(saved);
        Assert.True(vm.IsEditing);
        Assert.Contains("SSH 公钥为必填项。", vm.EditorError);
    }

    [Fact]
    public void SaveDraft_Card_CreatesCardDetail()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Card);
        vm.EditorDraft!.Name = "Travel Card";
        vm.EditorDraft.Card.CardholderName = "Ming";
        vm.EditorDraft.Card.Number = "4111111111111111";
        vm.EditorDraft.Card.ExpMonth = "08";
        vm.EditorDraft.Card.ExpYear = "2030";
        vm.EditorDraft.Card.Code = "123";
        vm.EditorDraft.Card.Brand = "Visa";

        var saved = vm.SaveDraft();

        Assert.True(saved);
        var detail = Assert.IsType<CardDetail>(vm.Detail);
        Assert.Equal("Ming", detail.Cardholder);
        Assert.Equal("08/2030", detail.Expiry);
        Assert.Equal("Visa", detail.Brand);
    }

    [Fact]
    public void SaveDraft_FilterExcludesNewType_SwitchesToAllItemsSoNewItemIsVisible()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Type && f.TypeFilter == VaultItemKind.Login);
        vm.BeginAdd(VaultItemKind.Card);
        vm.EditorDraft!.Name = "Filter Card";

        var saved = vm.SaveDraft();

        Assert.True(saved);
        Assert.Equal(FilterKind.AllItems, vm.SelectedFilter!.Kind);
        Assert.Equal("vault:allitems", vm.SelectedFilterTag);
        Assert.Contains(vm.FilteredItems, item => item.Name == "Filter Card");
        Assert.Equal("Filter Card", vm.SelectedItem!.Name);
    }

    [Fact]
    public void SaveDraft_SearchTextExcludesNewItem_ClearsSearchSoNewItemIsVisible()
    {
        var vm = NewVm();
        vm.SearchText = "百度";
        vm.BeginAdd(VaultItemKind.Card);
        vm.EditorDraft!.Name = "Search Hidden Card";

        var saved = vm.SaveDraft();

        Assert.True(saved);
        Assert.Equal("", vm.SearchText);
        Assert.Equal("Search Hidden Card", vm.SelectedItem!.Name);
        Assert.Contains(vm.FilteredItems, item => item.Id == vm.SelectedItem.Id);
    }

    [Fact]
    public void SaveDraft_FolderId_UsesListFolderIdAndDetailFolderName()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Folder Login";
        vm.EditorDraft.FolderId = "f1";

        var saved = vm.SaveDraft();

        Assert.True(saved);
        Assert.Equal("f1", vm.SelectedItem!.FolderId);
        var detail = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.Equal("文件夹1", detail.FolderName);
    }

    [Fact]
    public void SaveDraft_PersistsInSharedMockServiceAcrossViewModels()
    {
        var service = new MockVaultUiService();
        var first = new VaultViewModel(service);
        first.BeginAdd(VaultItemKind.Login);
        first.EditorDraft!.Name = "Persistent Login";
        first.EditorDraft.Login.Username = "persisted";

        Assert.True(first.SaveDraft());

        var second = new VaultViewModel(service);
        var item = Assert.Single(second.Items, i => i.Name == "Persistent Login");
        second.SelectedItem = item;

        var detail = Assert.IsType<LoginDetail>(second.Detail);
        Assert.Equal("persisted", detail.Username);
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

    [Fact]
    public void CreateSend_TextDraft_AddsTextSendToList()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "一次性验证码";
        draft.Text = "123456";
        draft.DeletionDateLabel = "7 天";

        var created = vm.CreateSend(draft);

        Assert.True(created);
        Assert.Contains(vm.Items, s => s.Name == "一次性验证码" && s.Type == SendType.Text);
        Assert.Contains(vm.FilteredItems, s => s.Name == "一次性验证码");
    }

    [Fact]
    public void CreateSend_FileDraft_AddsFileSendToList()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "合同.zip";
        draft.FileName = "合同.zip";
        draft.DeletionDateLabel = "30 天";

        var created = vm.CreateSend(draft);

        Assert.True(created);
        Assert.Contains(vm.Items, s => s.Name == "合同.zip" && s.Type == SendType.File);
    }

    [Fact]
    public void CreateSend_MissingRequiredData_ReturnsFalse()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "";
        draft.Text = "";

        var created = vm.CreateSend(draft);

        Assert.False(created);
        Assert.DoesNotContain(vm.Items, s => string.IsNullOrWhiteSpace(s.Name));
    }

    [Fact]
    public void MarkMoreMenuOpened_StoresSelectedSend()
    {
        var vm = new SendViewModel(new MockSendUiService());
        var item = vm.Items[0];

        vm.MarkMoreMenuOpened(item);

        Assert.Equal(item, vm.SelectedMenuItem);
    }
}
