using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

// The fake controls search, facet, and date data independently of demonstration fixtures.
public sealed class FakeFacetVaultUiService : IVaultUiService
{
    private readonly List<CipherListItem> _items;

    public FakeFacetVaultUiService(List<CipherListItem> items) => _items = items;

    public IReadOnlyList<CipherListItem> GetItems() => _items;

    public CipherAccessPolicy GetAccessPolicy(string id) => new(id, false);

    public CipherDetail GetDetail(string id, bool includeSensitive) => new LoginDetail
    {
        Id = id,
        Name = _items.First(i => i.Id == id).Name,
        Username = string.Empty,
        Password = string.Empty,
        SensitiveUnlocked = includeSensitive,
    };

    public IReadOnlyList<FilterNode> GetFilters() => new[]
    {
        new FilterNode { Label = "所有项目", Kind = FilterKind.AllItems },
    };

    public CipherEditorDraft GetDraft(string id) => CipherEditorDraft.CreateDefault(VaultItemKind.Login);

    public int SkippedCipherCount => 0;
    public bool DataStale => false;

    public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default) => Task.FromResult("x");
    public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreCipherAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SyncAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.CompletedTask;
}

public class VaultViewModelFilterTests
{
    private static CipherListItem MakeItem(
        string id, string name, bool hasTotp = false, bool hasAttachment = false, bool hasUri = false,
        bool favorite = false, DateTimeOffset? revision = null, DateTimeOffset? creation = null,
        VaultItemKind kind = VaultItemKind.Login) => new()
    {
        Id = id,
        Name = name,
        Favorite = favorite,
        Kind = kind,
        SearchHaystack = name.ToLowerInvariant(),
        HasTotp = hasTotp,
        HasAttachment = hasAttachment,
        HasUri = hasUri,
        RevisionDate = revision ?? DateTimeOffset.UnixEpoch,
        CreationDate = creation ?? DateTimeOffset.UnixEpoch,
    };

    private static VaultViewModel NewVm(List<CipherListItem> items) => new(new FakeFacetVaultUiService(items));

    [Fact]
    public void FacetTotp_True_FiltersToOnlyTotpItems()
    {
        var items = new List<CipherListItem>
        {
            MakeItem("1", "Alpha", hasTotp: true),
            MakeItem("2", "Beta", hasTotp: false),
            MakeItem("3", "Gamma", hasTotp: true),
        };
        var vm = NewVm(items);

        vm.FacetTotp = true;

        Assert.Equal(2, vm.FilteredItems.Count);
        Assert.All(vm.FilteredItems, i => Assert.True(i.HasTotp));
    }

    [Fact]
    public void SearchText_MatchesHaystack_NarrowsResults_StacksWithFacet()
    {
        var items = new List<CipherListItem>
        {
            MakeItem("1", "GitHub Login", hasTotp: true),
            MakeItem("2", "GitHub Backup", hasTotp: false),
            MakeItem("3", "Another Site", hasTotp: true),
        };
        var vm = NewVm(items);

        vm.FacetTotp = true;
        vm.SearchText = "github";

        var only = Assert.Single(vm.FilteredItems);
        Assert.Equal("1", only.Id);
    }

    [Fact]
    public void SelectedSort_RevisionDesc_FlattensGroupsAndOrdersByRevisionDescending()
    {
        var now = DateTimeOffset.UtcNow;
        var items = new List<CipherListItem>
        {
            MakeItem("1", "Old", kind: VaultItemKind.Login, revision: now.AddDays(-5)),
            MakeItem("2", "New", kind: VaultItemKind.Card, revision: now),
            MakeItem("3", "Mid", kind: VaultItemKind.Note, revision: now.AddDays(-2)),
        };
        var vm = NewVm(items);

        vm.SelectedSort = VaultSortKey.RevisionDesc;

        var group = Assert.Single(vm.GroupedItems);
        Assert.False(group.ShowHeader);
        Assert.Equal(new[] { "2", "3", "1" }, group.Select(i => i.Id));
    }

    [Fact]
    public void SelectedSort_NameAsc_ExplicitlyRemovesDateGroupsAndSortsAllTypesTogether()
    {
        var items = new List<CipherListItem>
        {
            MakeItem("1", "Zeta", kind: VaultItemKind.Login),
            MakeItem("2", "Alpha", kind: VaultItemKind.Card),
        };
        var vm = NewVm(items);

        Assert.Equal(VaultSortKey.RecentUse, vm.SelectedSort);
        vm.SelectedSort = VaultSortKey.NameAsc;

        var group = Assert.Single(vm.GroupedItems);
        Assert.False(group.ShowHeader);
        Assert.Equal(new[] { "2", "1" }, group.Select(item => item.Id));
    }

    [Fact]
    public void RecentUse_DoesNotReorderVisibleResultsUntilExplicitSortChange()
    {
        var vm = NewVm([MakeItem("first", "Alpha"), MakeItem("used", "Zeta")]);
        vm.SelectedItem = vm.FilteredItems[0];
        var before = vm.FilteredItems.Select(item => item.Id).ToArray();

        vm.RecordRecentUse("used");

        Assert.NotNull(vm.GetLastUsedAt("used"));
        Assert.Equal(before, vm.FilteredItems.Select(item => item.Id));
        Assert.Equal("first", vm.SelectedItem?.Id);

        vm.SelectedSort = VaultSortKey.NameAsc;
        vm.SelectedSort = VaultSortKey.RecentUse;

        Assert.Equal(new[] { "used", "first" }, vm.FilteredItems.Select(item => item.Id));
        Assert.Equal(new[] { "今天", "更早" }, vm.GroupedItems.Select(group => group.Key));
        Assert.Equal("first", vm.SelectedItem?.Id);
    }

    [Fact]
    public void RecentUse_EnteringSearchRefreshesTheUsageSnapshot()
    {
        var vm = NewVm([MakeItem("first", "Alpha"), MakeItem("used", "Zeta")]);
        vm.SelectedItem = vm.FilteredItems[0];
        vm.RecordRecentUse("used");
        Assert.Equal(new[] { "first", "used" }, vm.FilteredItems.Select(item => item.Id));

        vm.SearchText = "a";

        Assert.Equal(new[] { "used", "first" }, vm.FilteredItems.Select(item => item.Id));
        Assert.Equal("first", vm.SelectedItem?.Id);
        Assert.False(Assert.Single(vm.GroupedItems).ShowHeader);
    }

    [Fact]
    public void RecentUse_UnknownItemDoesNotCreateUsageMetadata()
    {
        var vm = NewVm([MakeItem("existing", "Alpha")]);

        vm.RecordRecentUse("missing");

        Assert.Null(vm.GetLastUsedAt("missing"));
        Assert.Null(vm.GetLastUsedAt("existing"));
    }

    [Theory]
    [InlineData(0, VaultSortKey.RecentUse)]
    [InlineData(1, VaultSortKey.NameAsc)]
    [InlineData(2, VaultSortKey.RevisionDesc)]
    [InlineData(3, VaultSortKey.NameDesc)]
    [InlineData(4, VaultSortKey.CreationDesc)]
    public void SelectedSortIndex_MapsDisplayOrderWithoutChangingPersistedEnumValues(int index, VaultSortKey sort)
    {
        var vm = NewVm([MakeItem("item", "Alpha")]);

        vm.SelectedSortIndex = index;

        Assert.Equal(sort, vm.SelectedSort);
        Assert.Equal(index, vm.SelectedSortIndex);
    }

    [Fact]
    public void NewEditorDraft_GeneratedPasswordCountsAsUnsavedBeforeItHasAName()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.BeginAdd(VaultItemKind.Login);
        Assert.False(vm.HasUnsavedEditorChanges);

        vm.EditorDraft!.Login.Password = "generated-password";

        Assert.True(vm.HasUnsavedEditorChanges);
        vm.EditorDraft.Login.Password = string.Empty;
        Assert.False(vm.HasUnsavedEditorChanges);
    }

    [Fact]
    public async Task ExistingEditorDraft_NestedFieldChangeAndRevertTrackUnsavedState()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        var login = vm.FilteredItems.First(item => item.Kind == VaultItemKind.Login);
        var result = await vm.BeginEditAsync(login.Id, _ => Task.FromResult<string?>(null));
        Assert.True(result.IsSuccess);
        Assert.False(vm.HasUnsavedEditorChanges);
        var original = vm.EditorDraft!.Login.Username;
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        vm.EditorDraft.Login.Username = "changed-user";

        Assert.True(vm.HasUnsavedEditorChanges);
        Assert.Contains(nameof(VaultViewModel.HasUnsavedEditorChanges), notifications);
        vm.EditorDraft.Login.Username = original;
        Assert.False(vm.HasUnsavedEditorChanges);
    }

    [Fact]
    public async Task ExistingLogin_TracksOnlyActualPasswordChangesAndRecognizesRevert()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        var login = vm.FilteredItems.First(item => item.Kind == VaultItemKind.Login);
        Assert.False(vm.HasEditorPasswordChange);
        Assert.True((await vm.BeginEditAsync(login.Id, _ => Task.FromResult<string?>(null))).IsSuccess);
        Assert.False(vm.HasEditorPasswordChange);
        var originalPassword = vm.EditorDraft!.Login.Password;

        vm.EditorDraft.Login.Username = "changed-username";
        Assert.False(vm.HasEditorPasswordChange);
        vm.EditorDraft.Login.Password = "changed-password";
        Assert.True(vm.HasEditorPasswordChange);
        vm.EditorDraft.Login.Password = originalPassword;
        Assert.False(vm.HasEditorPasswordChange);
    }

    [Fact]
    public async Task ExistingLogin_CancellingPasswordChangeResetsTheChangeFlag()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        var login = vm.FilteredItems.First(item => item.Kind == VaultItemKind.Login);
        Assert.True((await vm.BeginEditAsync(login.Id, _ => Task.FromResult<string?>(null))).IsSuccess);
        vm.EditorDraft!.Login.Password = "changed-password";
        Assert.True(vm.HasEditorPasswordChange);

        vm.CancelEdit();

        Assert.False(vm.HasEditorPasswordChange);
        Assert.Null(vm.EditorDraft);
        Assert.True((await vm.BeginEditAsync(login.Id, _ => Task.FromResult<string?>(null))).IsSuccess);
        Assert.False(vm.HasEditorPasswordChange);
    }

    [Fact]
    public void NewLogin_PopulatedPasswordDoesNotCountAsChangingAnExistingPassword()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.BeginAdd(VaultItemKind.Login);
        Assert.False(vm.HasEditorPasswordChange);

        vm.EditorDraft!.Login.Password = "generated-password";

        Assert.True(vm.HasUnsavedEditorChanges);
        Assert.False(vm.HasEditorPasswordChange);
        vm.CancelEdit();
        Assert.False(vm.HasEditorPasswordChange);
    }

    [Fact]
    public async Task ExistingPasswordlessLogin_TreatsEmptyAndWhitespaceAsTheSamePassword()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Passwordless login";
        var created = await vm.SaveDraftAsync();
        Assert.True(created.IsSuccess);
        Assert.True((await vm.BeginEditAsync(created.Value!, _ => Task.FromResult<string?>(null))).IsSuccess);
        Assert.False(vm.HasEditorPasswordChange);

        vm.EditorDraft!.Login.Password = " \t ";

        Assert.False(vm.HasEditorPasswordChange);
        vm.EditorDraft.Login.Password = "new-password";
        Assert.True(vm.HasEditorPasswordChange);
        vm.EditorDraft.Login.Password = string.Empty;
        Assert.False(vm.HasEditorPasswordChange);
    }

    [Fact]
    public async Task ExistingNonLogin_DoesNotReportAChangeFromItsInactiveLoginDraft()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        var card = vm.FilteredItems.First(item => item.Kind == VaultItemKind.Card);
        Assert.True((await vm.BeginEditAsync(card.Id, _ => Task.FromResult<string?>(null))).IsSuccess);

        vm.EditorDraft!.Login.Password = "inactive-password";

        Assert.False(vm.HasEditorPasswordChange);
    }

    [Fact]
    public void NewEditorDraft_ObservesFieldsAddedAfterTheEditorOpens()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.BeginAdd(VaultItemKind.Login);
        var field = new CustomFieldEditorDraft();
        vm.EditorDraft!.CustomFields.Add(field);
        Assert.False(vm.HasUnsavedEditorChanges);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        field.Value = "custom-value";

        Assert.True(vm.HasUnsavedEditorChanges);
        Assert.Contains(nameof(VaultViewModel.HasUnsavedEditorChanges), notifications);
        vm.EditorDraft.CustomFields.Remove(field);
        Assert.False(vm.HasUnsavedEditorChanges);
        notifications.Clear();
        field.Value = "detached-value";
        Assert.DoesNotContain(nameof(VaultViewModel.HasUnsavedEditorChanges), notifications);
        vm.CancelEdit();
        Assert.False(vm.HasUnsavedEditorChanges);
    }

    [Theory]
    [InlineData("vault:allitems", null, "vault:allitems")]
    [InlineData("vault:folder:f1", "f1", "vault:folder:f1")]
    [InlineData("vault:folder:f1", null, "vault:allitems")]
    [InlineData("vault:favorites", null, "vault:allitems")]
    [InlineData("vault:type:Card", null, "vault:allitems")]
    public async Task SaveNewItem_ClearsExcludingRefinementsAndSelectsTheAppropriateScope(
        string startingScope, string? folderId, string expectedScope)
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.SelectFilterByTag(startingScope);
        vm.SearchText = "unmatched query";
        vm.SelectedTypeIndex = 2;
        vm.FacetTotp = vm.FacetAttachment = vm.FacetUri = vm.FacetFavoriteOnly = true;
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Created login";
        vm.EditorDraft.FolderId = folderId;

        var result = await vm.SaveDraftAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedScope, vm.SelectedFilterTag);
        Assert.Empty(vm.SearchText);
        Assert.Equal(0, vm.SelectedTypeIndex);
        Assert.False(vm.CurrentFacets.Any);
        Assert.Contains(vm.FilteredItems, item => item.Id == result.Value);
        Assert.Equal(result.Value, vm.SelectedItem?.Id);
        Assert.Equal(result.Value, vm.Detail?.Id);
        Assert.False(vm.IsEditing);
        Assert.False(vm.HasUnsavedEditorChanges);
        Assert.Null(vm.EditorDraft);
    }

    [Fact]
    public async Task SaveNewItem_PreservesCompatibleFacets()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.FacetTotp = vm.FacetUri = vm.FacetFavoriteOnly = true;
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Created favorite";
        vm.EditorDraft.Favorite = true;
        vm.EditorDraft.Login.Totp = "JBSWY3DPEHPK3PXP";
        vm.EditorDraft.Login.Uris[0].Uri = "https://example.com";

        var result = await vm.SaveDraftAsync();

        Assert.True(result.IsSuccess);
        Assert.True(vm.FacetTotp);
        Assert.True(vm.FacetUri);
        Assert.True(vm.FacetFavoriteOnly);
        Assert.Equal(result.Value, vm.SelectedItem?.Id);
        Assert.Contains(vm.FilteredItems, item => item.Id == result.Value);
    }

    [Fact]
    public async Task SaveExistingItem_PreservesItsSearchAndTypeFilter()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        var login = vm.FilteredItems.First(item => item.Kind == VaultItemKind.Login);
        vm.SearchText = login.Name;
        vm.SelectedTypeIndex = 1;
        Assert.True((await vm.BeginEditAsync(login.Id, _ => Task.FromResult<string?>(null))).IsSuccess);
        vm.EditorDraft!.Notes = "Changed note";

        var result = await vm.SaveDraftAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(login.Name, vm.SearchText);
        Assert.Equal(1, vm.SelectedTypeIndex);
        Assert.Equal(login.Id, vm.SelectedItem?.Id);
    }

    [Fact]
    public void HasActiveRefinement_ReflectsFacetsAndSearchText()
    {
        var items = new List<CipherListItem> { MakeItem("1", "Alpha") };
        var vm = NewVm(items);

        Assert.False(vm.HasActiveRefinement);

        vm.FacetUri = true;
        Assert.True(vm.HasActiveRefinement);

        vm.FacetUri = false;
        vm.SearchText = "al";
        Assert.True(vm.HasActiveRefinement);
    }
}
