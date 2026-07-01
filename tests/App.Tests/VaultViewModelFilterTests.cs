using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

// 专用 fake:精确控制 haystack/分面/日期字段,不依赖 MockVaultUiService 固定数据。
public sealed class FakeFacetVaultUiService : IVaultUiService
{
    private readonly List<CipherListItem> _items;

    public FakeFacetVaultUiService(List<CipherListItem> items) => _items = items;

    public IReadOnlyList<CipherListItem> GetItems() => _items;

    public CipherDetail GetDetail(string id) => new LoginDetail
    {
        Id = id,
        Name = _items.First(i => i.Id == id).Name,
        Username = string.Empty,
        Password = string.Empty,
    };

    public IReadOnlyList<FilterNode> GetFilters() => new[]
    {
        new FilterNode { Label = "所有项目", Kind = FilterKind.AllItems },
    };

    public CipherEditorDraft GetDraft(string id) => CipherEditorDraft.CreateDefault(VaultItemKind.Login);

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
        Assert.Equal(new[] { "2", "3", "1" }, group.Items.Select(i => i.Id));
    }

    [Fact]
    public void SelectedSort_NameAsc_Default_KeepsExistingMultiGroupStructure()
    {
        var items = new List<CipherListItem>
        {
            MakeItem("1", "Zeta", kind: VaultItemKind.Login),
            MakeItem("2", "Alpha", kind: VaultItemKind.Card),
        };
        var vm = NewVm(items);

        Assert.Equal(VaultSortKey.NameAsc, vm.SelectedSort);
        // 聚合视图(AllItems)按类型分组,固定组序,ShowHeader==true。
        Assert.Equal(2, vm.GroupedItems.Count);
        Assert.All(vm.GroupedItems, g => Assert.True(g.ShowHeader));
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
