using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class VaultRecycleBinTests
{
    private sealed class CapturingVaultUiService : IVaultUiService
    {
        private readonly MockVaultUiService _inner = new();
        private readonly IReadOnlyList<CipherListItem> _items;
        public IReadOnlyCollection<string>? LastDeleteIds { get; private set; }
        public bool? LastDeletePermanent { get; private set; }
        public int DeleteCallCount { get; private set; }

        public CapturingVaultUiService(IReadOnlyList<CipherListItem> items) => _items = items;

        public IReadOnlyList<CipherListItem> GetItems() => _items;
        public CipherDetail GetDetail(string id) => _inner.GetDetail(id);
        public IReadOnlyList<FilterNode> GetFilters() => _inner.GetFilters();  // 含 Trash 节点
        public CipherEditorDraft GetDraft(string id) => _inner.GetDraft(id);
        public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default) => Task.FromResult("x");
        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreCipherAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
        { DeleteCallCount++; LastDeleteIds = ids; LastDeletePermanent = permanent; return Task.CompletedTask; }
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static CipherListItem Item(string id, bool deleted) =>
        new() { Id = id, Name = id, IsDeleted = deleted };

    private static (VaultViewModel vm, CapturingVaultUiService svc) NewTrashVm(params CipherListItem[] items)
    {
        var svc = new CapturingVaultUiService(items);
        var vm = new VaultViewModel(svc);
        vm.SelectFilterByTag("vault:trash");
        return (vm, svc);
    }

    [Fact]
    public void TrashCount_And_Flags()
    {
        var (vm, _) = NewTrashVm(Item("a", true), Item("b", true), Item("c", false));
        Assert.Equal(2, vm.TrashCount);
        Assert.True(vm.HasTrashItems);
        Assert.True(vm.IsTrashFilterSelected);
        Assert.False(vm.IsTrashEmpty);
        Assert.True(vm.CanEmptyRecycleBin);
        Assert.Equal("回收站 (2)", vm.TrashHeader);
    }

    [Fact]
    public void EmptyTrash_Flags()
    {
        var (vm, _) = NewTrashVm(Item("c", false));   // 0 deleted
        Assert.Equal(0, vm.TrashCount);
        Assert.True(vm.IsTrashEmpty);
        Assert.False(vm.CanEmptyRecycleBin);
    }

    [Fact]
    public async Task EmptyRecycleBin_HardDeletes_AllTrashedIds()
    {
        var (vm, svc) = NewTrashVm(Item("d1", true), Item("d2", true), Item("keep", false));
        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);
        Assert.Equal(1, svc.DeleteCallCount);
        Assert.Equal(true, svc.LastDeletePermanent);
        Assert.Equal(new[] { "d1", "d2" }, svc.LastDeleteIds!.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task EmptyRecycleBin_NoOp_WhenEmpty()
    {
        var (vm, svc) = NewTrashVm(Item("keep", false));
        await vm.EmptyRecycleBinCommand.ExecuteAsync(null);
        Assert.Equal(0, svc.DeleteCallCount);
    }
}
