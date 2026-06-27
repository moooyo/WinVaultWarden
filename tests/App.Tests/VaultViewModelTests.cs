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

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
    }

    [Fact]
    public void CopyPrimary_Login_CopiesPasswordOrUsername()
    {
        var clipboard = new RecordingClipboard();
        var service = new MockVaultUiService();
        var vm = new VaultViewModel(service, clipboard);
        var login = (LoginDetail)service.GetDetail("1");
        var expected = string.IsNullOrEmpty(login.Password) ? login.Username : login.Password;

        vm.CopyPrimaryCommand.Execute("1");

        Assert.Equal(expected, clipboard.Text);
    }

    [Fact]
    public void CopyPrimary_NullId_DoesNothing()
    {
        var clipboard = new RecordingClipboard();
        var vm = new VaultViewModel(new MockVaultUiService(), clipboard);

        vm.CopyPrimaryCommand.Execute(null);

        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task ToggleFavorite_FlipsFavoriteState()
    {
        var vm = NewVm();
        var item = vm.Items.First(i => !i.IsDeleted);
        var before = item.Favorite;

        await vm.ToggleFavoriteAsync(item.Id);

        Assert.Equal(!before, vm.Items.First(i => i.Id == item.Id).Favorite);
    }

    [Fact]
    public void Initialize_LoadsItemsAndFilters()
    {
        var vm = NewVm();
        Assert.Equal(6, vm.Items.Count);
        Assert.NotEmpty(vm.Filters);
    }

    [Fact]
    public void FolderFilters_ReturnsOnlyFolderFilters()
    {
        var vm = NewVm();

        var folders = vm.FolderFilters.ToArray();

        Assert.NotEmpty(folders);
        Assert.All(folders, folder => Assert.Equal(FilterKind.Folder, folder.Kind));
        Assert.Equal(vm.Filters.Where(f => f.Kind == FilterKind.Folder).Select(f => f.FolderId), folders.Select(f => f.FolderId));
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
    public async Task SaveDraft_MissingName_ReturnsFalseAndKeepsEditing()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "";

        var saved = await vm.SaveDraftAsync();

        Assert.False(saved);
        Assert.True(vm.IsEditing);
        Assert.Equal("项目名称为必填项。", vm.EditorError);
    }

    [Fact]
    public async Task SaveDraft_Login_AddsItemSelectsItAndShowsDetail()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "GitHub";
        vm.EditorDraft.Login.Username = "octo";
        vm.EditorDraft.Login.Password = "secret";
        vm.EditorDraft.Login.Uris[0].Uri = "https://github.com";

        var saved = await vm.SaveDraftAsync();

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
    public async Task SaveDraft_Ssh_RequiresKeyFields()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Ssh);
        vm.EditorDraft!.Name = "prod";
        vm.EditorDraft.SshKey.PrivateKey = "private";

        var saved = await vm.SaveDraftAsync();

        Assert.False(saved);
        Assert.True(vm.IsEditing);
        Assert.Contains("SSH 公钥为必填项。", vm.EditorError);
    }

    [Fact]
    public async Task SaveDraft_Card_CreatesCardDetail()
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

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        var detail = Assert.IsType<CardDetail>(vm.Detail);
        Assert.Equal("Ming", detail.Cardholder);
        Assert.Equal("08/2030", detail.Expiry);
        Assert.Equal("Visa", detail.Brand);
    }

    [Fact]
    public async Task SaveDraft_Identity_CreatesIdentityDetailWithExtendedFields()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Identity);
        vm.EditorDraft!.Name = "Profile";
        vm.EditorDraft.Identity.FirstName = "Ming";
        vm.EditorDraft.Identity.LastName = "Chen";
        vm.EditorDraft.Identity.Username = "mingc";
        vm.EditorDraft.Identity.Company = "Northwind";
        vm.EditorDraft.Identity.Email = "ming@example.com";
        vm.EditorDraft.Identity.Phone = "123456";
        vm.EditorDraft.Identity.Address1 = "Road 1";

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        var detail = Assert.IsType<IdentityDetail>(vm.Detail);
        Assert.Equal("Ming Chen", detail.FullName);
        Assert.Equal("mingc", detail.Username);
        Assert.Equal("Northwind", detail.Company);
        Assert.Equal("ming@example.com", detail.Email);
        Assert.Equal("123456", detail.Phone);
        Assert.Equal("Road 1", detail.Address);
    }

    [Fact]
    public async Task SaveDraft_Note_CreatesNoteDetail()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Note);
        vm.EditorDraft!.Name = "Recovery Note";
        vm.EditorDraft.Notes = "keep offline";

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        var detail = Assert.IsType<NoteDetail>(vm.Detail);
        Assert.Equal("Recovery Note", detail.Name);
        Assert.Equal("keep offline", detail.Content);
        Assert.Equal("keep offline", detail.Notes);
    }

    [Fact]
    public async Task SaveDraft_Ssh_CreatesSshDetail()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Ssh);
        vm.EditorDraft!.Name = "Production SSH";
        vm.EditorDraft.SshKey.PublicKey = "ssh-ed25519 AAAA";
        vm.EditorDraft.SshKey.PrivateKey = "private-key";
        vm.EditorDraft.SshKey.KeyFingerprint = "SHA256:abc";

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        var detail = Assert.IsType<SshDetail>(vm.Detail);
        Assert.Equal("ssh-ed25519 AAAA", detail.PublicKey);
        Assert.Equal("private-key", detail.PrivateKey);
        Assert.Equal("SHA256:abc", detail.Fingerprint);
    }

    [Fact]
    public async Task SaveDraft_CommonMetadataAndCustomFields_RoundTripToDetail()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Metadata Login";
        vm.EditorDraft.Favorite = true;
        vm.EditorDraft.Reprompt = true;
        vm.EditorDraft.CustomFields.Add(new CustomFieldEditorDraft
        {
            Name = "Text field",
            Type = CipherEditorFieldType.Text,
            Value = "visible",
        });
        vm.EditorDraft.CustomFields.Add(new CustomFieldEditorDraft
        {
            Name = "Hidden field",
            Type = CipherEditorFieldType.Hidden,
            Value = "secret",
        });
        vm.EditorDraft.CustomFields.Add(new CustomFieldEditorDraft
        {
            Name = "Boolean field",
            Type = CipherEditorFieldType.Boolean,
            BooleanValue = true,
        });

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        var detail = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.True(detail.Favorite);
        Assert.True(detail.Reprompt);
        Assert.Collection(
            detail.CustomFields,
            field =>
            {
                Assert.Equal("Text field", field.Label);
                Assert.Equal("visible", field.Value);
                Assert.Equal(CipherEditorFieldType.Text, field.Type);
                Assert.False(field.IsSecret);
            },
            field =>
            {
                Assert.Equal("Hidden field", field.Label);
                Assert.Equal("secret", field.Value);
                Assert.Equal(CipherEditorFieldType.Hidden, field.Type);
                Assert.True(field.IsSecret);
            },
            field =>
            {
                Assert.Equal("Boolean field", field.Label);
                Assert.Equal("True", field.Value);
                Assert.Equal(CipherEditorFieldType.Boolean, field.Type);
                Assert.False(field.IsSecret);
            });
    }

    [Fact]
    public async Task SaveDraft_FilterExcludesNewType_SwitchesToAllItemsSoNewItemIsVisible()
    {
        var vm = NewVm();
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Type && f.TypeFilter == VaultItemKind.Login);
        vm.BeginAdd(VaultItemKind.Card);
        vm.EditorDraft!.Name = "Filter Card";

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        Assert.Equal(FilterKind.AllItems, vm.SelectedFilter!.Kind);
        Assert.Equal("vault:allitems", vm.SelectedFilterTag);
        Assert.Contains(vm.FilteredItems, item => item.Name == "Filter Card");
        Assert.Equal("Filter Card", vm.SelectedItem!.Name);
    }

    [Fact]
    public async Task SaveDraft_SearchTextExcludesNewItem_ClearsSearchSoNewItemIsVisible()
    {
        var vm = NewVm();
        vm.SearchText = "百度";
        vm.BeginAdd(VaultItemKind.Card);
        vm.EditorDraft!.Name = "Search Hidden Card";

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        Assert.Equal("", vm.SearchText);
        Assert.Equal("Search Hidden Card", vm.SelectedItem!.Name);
        Assert.Contains(vm.FilteredItems, item => item.Id == vm.SelectedItem.Id);
    }

    [Fact]
    public async Task SaveDraft_FolderId_UsesListFolderIdAndDetailFolderName()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Folder Login";
        vm.EditorDraft.FolderId = "f1";

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        Assert.Equal("f1", vm.SelectedItem!.FolderId);
        var detail = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.Equal("文件夹1", detail.FolderName);
    }

    [Fact]
    public async Task SaveDraft_PersistsInSharedMockServiceAcrossViewModels()
    {
        var service = new MockVaultUiService();
        var first = new VaultViewModel(service);
        first.BeginAdd(VaultItemKind.Login);
        first.EditorDraft!.Name = "Persistent Login";
        first.EditorDraft.Login.Username = "persisted";

        Assert.True(await first.SaveDraftAsync());

        var second = new VaultViewModel(service);
        var item = Assert.Single(second.Items, i => i.Name == "Persistent Login");
        second.SelectedItem = item;

        var detail = Assert.IsType<LoginDetail>(second.Detail);
        Assert.Equal("persisted", detail.Username);
    }

    [Fact]
    public void BeginEdit_LoadsDraftFromItemAndSetsEditTitle()
    {
        var vm = NewVm();
        vm.SelectedItem = vm.Items.First(i => i.Id == "1");

        vm.BeginEdit("1");

        Assert.True(vm.IsEditing);
        Assert.NotNull(vm.EditorDraft);
        Assert.Equal("百度网盘", vm.EditorDraft!.Name);
        Assert.Equal("admin", vm.EditorDraft.Login.Username);
        Assert.Equal("编辑登录", vm.EditorTitle);
    }

    [Fact]
    public async Task SaveDraftAsync_Edit_UpdatesItemInPlaceWithoutDuplicating()
    {
        var vm = NewVm();
        var before = vm.Items.Count;
        vm.SelectedItem = vm.Items.First(i => i.Id == "1");
        vm.BeginEdit("1");
        vm.EditorDraft!.Name = "百度网盘改名";

        var saved = await vm.SaveDraftAsync();

        Assert.True(saved);
        Assert.Equal(before, vm.Items.Count);
        Assert.Contains(vm.Items, i => i.Id == "1" && i.Name == "百度网盘改名");
        Assert.Equal("百度网盘改名", vm.SelectedItem!.Name);
    }

    [Fact]
    public async Task SoftDeleteAsync_MovesItemToTrashAndClearsSelection()
    {
        var vm = NewVm();
        vm.SelectedItem = vm.Items.First(i => i.Id == "1");

        var ok = await vm.SoftDeleteAsync("1");

        Assert.True(ok);
        Assert.Null(vm.SelectedItem);
        Assert.True(vm.Items.First(i => i.Id == "1").IsDeleted);
        Assert.DoesNotContain(vm.FilteredItems, i => i.Id == "1"); // AllItems hides trash
    }

    [Fact]
    public async Task RestoreAsync_BringsItemBackFromTrash()
    {
        var vm = NewVm();

        var ok = await vm.RestoreAsync("6");

        Assert.True(ok);
        Assert.False(vm.Items.First(i => i.Id == "6").IsDeleted);
    }

    [Fact]
    public async Task PermanentDeleteAsync_RemovesItemEntirely()
    {
        var vm = NewVm();

        var ok = await vm.PermanentDeleteAsync("6");

        Assert.True(ok);
        Assert.DoesNotContain(vm.Items, i => i.Id == "6");
    }

    [Fact]
    public async Task SaveFolderAsync_AddsFolderFilterAndRaisesFoldersChanged()
    {
        var vm = NewVm();
        var raised = false;
        vm.FoldersChanged += (_, _) => raised = true;

        var ok = await vm.SaveFolderAsync(null, "新建文件夹");

        Assert.True(ok);
        Assert.True(raised);
        Assert.Contains(vm.Filters, f => f.Kind == FilterKind.Folder && f.Label == "新建文件夹");
    }

    [Fact]
    public async Task DeleteFolderAsync_RemovesFolderFilterAndRaisesFoldersChanged()
    {
        var vm = NewVm();
        var raised = false;
        vm.FoldersChanged += (_, _) => raised = true;

        var ok = await vm.DeleteFolderAsync("f1");

        Assert.True(ok);
        Assert.True(raised);
        Assert.DoesNotContain(vm.Filters, f => f.Kind == FilterKind.Folder && f.FolderId == "f1");
    }

    [Fact]
    public async Task SaveDraftAsync_WhenServiceThrows_SetsOperationErrorAndReturnsFalse()
    {
        var vm = new VaultViewModel(new ThrowingVaultUiService());
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Boom";

        var saved = await vm.SaveDraftAsync();

        Assert.False(saved);
        Assert.Equal("boom", vm.OperationError);
        Assert.True(vm.IsEditing);       // stays in editor so user can retry
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveDraftAsync_WhenAlreadyBusy_ShortCircuitsBeforeCallingService()
    {
        var vm = new VaultViewModel(new ThrowingVaultUiService());
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "X";
        vm.IsBusy = true; // 模拟已有写入在途(防快速双击重复创建)

        var result = await vm.SaveDraftAsync();

        Assert.False(result);
        Assert.Equal(string.Empty, vm.OperationError); // 服务未被调用(否则 ThrowingVaultUiService 会抛出并置错误)
    }

    [Fact]
    public async Task SoftDeleteAsync_WhenAlreadyBusy_ShortCircuitsBeforeCallingService()
    {
        var vm = new VaultViewModel(new ThrowingVaultUiService());
        vm.IsBusy = true;

        var result = await vm.SoftDeleteAsync("1");

        Assert.False(result);
        Assert.Equal(string.Empty, vm.OperationError);
    }
}

public sealed class ThrowingVaultUiService : IVaultUiService
{
    private readonly MockVaultUiService _inner = new();
    public IReadOnlyList<CipherListItem> GetItems() => _inner.GetItems();
    public CipherDetail GetDetail(string id) => _inner.GetDetail(id);
    public IReadOnlyList<FilterNode> GetFilters() => _inner.GetFilters();
    public CipherEditorDraft GetDraft(string id) => _inner.GetDraft(id);
    public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task RestoreCipherAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    public Task SyncAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");
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
