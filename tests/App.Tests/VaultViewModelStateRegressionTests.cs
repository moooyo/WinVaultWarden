using System.Collections.Specialized;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public sealed class VaultViewModelStateRegressionTests
{
    [Fact]
    public void OnlyDeletedItems_ShowVaultEmptyOutsideTrashWithoutOverlayingTrashContents()
    {
        var service = new MutableStateVaultUiService
        {
            ItemsOverride = [Item("deleted", deleted: true)],
        };
        var viewModel = new VaultViewModel(service);

        AssertEmptyStates(viewModel, vaultEmpty: true, noResults: false, trashEmpty: false);

        viewModel.SelectFilterByTag("vault:trash");

        Assert.Single(viewModel.FilteredItems);
        AssertEmptyStates(viewModel, vaultEmpty: false, noResults: false, trashEmpty: false);
    }

    [Fact]
    public void EmptyTrash_ShowsOnlyTrashEmptyState()
    {
        var service = new MutableStateVaultUiService
        {
            ItemsOverride = [Item("active", deleted: false)],
        };
        var viewModel = new VaultViewModel(service);

        viewModel.SelectFilterByTag("vault:trash");

        Assert.Empty(viewModel.FilteredItems);
        AssertEmptyStates(viewModel, vaultEmpty: false, noResults: false, trashEmpty: true);
    }

    [Fact]
    public void UnmatchedSearch_ShowsOnlyNoResultsState()
    {
        var service = new MutableStateVaultUiService
        {
            ItemsOverride = [Item("active", deleted: false)],
        };
        var viewModel = new VaultViewModel(service);

        viewModel.SearchText = "no-such-value";

        Assert.Empty(viewModel.FilteredItems);
        AssertEmptyStates(viewModel, vaultEmpty: false, noResults: true, trashEmpty: false);
    }

    [Fact]
    public void ApplyFilter_RestoresVisibleSelectionWithoutClearingDetail()
    {
        var viewModel = new VaultViewModel(new MutableStateVaultUiService());
        viewModel.SelectedItem = viewModel.FilteredItems.First(item => item.Id == "1");
        var detailWasCleared = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(VaultViewModel.Detail) && viewModel.Detail is null)
                detailWasCleared = true;
        };
        viewModel.GroupedItems.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                viewModel.SelectedItem = null;
        };

        viewModel.SelectedSort = VaultSortKey.NameDesc;

        Assert.Equal("1", viewModel.SelectedItem?.Id);
        Assert.Equal("1", viewModel.Detail?.Id);
        Assert.False(detailWasCleared);
    }

    [Fact]
    public void ApplyFilter_ClearsSelectionWhenSelectedItemIsNoLongerVisible()
    {
        var viewModel = new VaultViewModel(new MutableStateVaultUiService());
        viewModel.SelectedItem = viewModel.FilteredItems.First(item => item.Id == "1");
        viewModel.GroupedItems.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                viewModel.SelectedItem = null;
        };

        viewModel.SearchText = "no-such-value";

        Assert.Null(viewModel.SelectedItem);
        Assert.Null(viewModel.Detail);
    }

    [Fact]
    public void RefreshFromSnapshot_CapturesSelectionBeforeCollectionsAreReloaded()
    {
        var viewModel = new VaultViewModel(new MutableStateVaultUiService());
        viewModel.SelectedItem = viewModel.FilteredItems.First(item => item.Id == "1");
        var detailWasCleared = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(VaultViewModel.Detail) && viewModel.Detail is null)
                detailWasCleared = true;
        };
        viewModel.Items.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                viewModel.SelectedItem = null;
        };

        viewModel.RefreshFromSnapshot();

        Assert.Equal("1", viewModel.SelectedItem?.Id);
        Assert.Equal("1", viewModel.Detail?.Id);
        Assert.False(detailWasCleared);
    }

    [Fact]
    public async Task SyncAsync_RefreshesSnapshotFlagsAfterSuccessfulSync()
    {
        var service = new MutableStateVaultUiService
        {
            DataStale = true,
            SkippedCipherCount = 2,
        };
        service.SyncMutation = () =>
        {
            service.DataStale = false;
            service.SkippedCipherCount = 0;
        };
        var viewModel = new VaultViewModel(service);

        var result = await viewModel.SyncAsync();

        Assert.True(result.IsSuccess);
        Assert.False(viewModel.DataStale);
        Assert.Equal(0, viewModel.SkippedCipherCount);
        Assert.False(viewModel.HasSkippedCiphers);
    }

    [Fact]
    public async Task SyncAsync_DoesNotResetRefinementWhenSelectionLeavesCurrentView()
    {
        var service = new MutableStateVaultUiService
        {
            ItemsOverride = [Item("1", deleted: false)],
        };
        var viewModel = new VaultViewModel(service)
        {
            SearchText = "1",
        };
        viewModel.SelectedItem = Assert.Single(viewModel.FilteredItems);
        service.SyncMutation = () =>
        {
            service.ItemsOverride = [Item("1", deleted: true)];
        };

        var result = await viewModel.SyncAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("1", viewModel.SearchText);
        Assert.Empty(viewModel.FilteredItems);
        Assert.Null(viewModel.SelectedItem);
        Assert.Null(viewModel.Detail);
    }

    [Fact]
    public async Task SaveDraft_CommittedRefreshFailure_ClosesEditorAndCannotBeResubmitted()
    {
        var service = new MutableStateVaultUiService
        {
            SaveException = new Core.Services.VaultRefreshException(
                "Remote write committed.",
                new HttpRequestException("Refresh failed.")),
        };
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Committed create";

        var result = await viewModel.SaveDraftAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Value);
        Assert.False(viewModel.IsEditing);
        Assert.Null(viewModel.EditorDraft);
        Assert.Equal(1, service.SaveCalls);
        Assert.True(viewModel.DataStale);
        Assert.True(viewModel.HasOperationWarning);
        Assert.False(viewModel.HasOperationError);

        var repeated = await viewModel.SaveDraftAsync();
        Assert.False(repeated.IsSuccess);
        Assert.Equal(1, service.SaveCalls);
    }

    [Fact]
    public async Task SuccessfulSync_ClearsCommittedWriteWarning()
    {
        var service = new MutableStateVaultUiService
        {
            SaveException = new Core.Services.VaultRefreshException(
                "Remote write committed.",
                new HttpRequestException("Refresh failed.")),
        };
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Committed create";
        await viewModel.SaveDraftAsync();
        service.SaveException = null;
        service.DataStale = false;

        viewModel.RefreshFromSnapshot();
        Assert.True(viewModel.DataStale);
        Assert.True(viewModel.HasOperationWarning);

        var result = await viewModel.SyncAsync();

        Assert.True(result.IsSuccess);
        Assert.False(viewModel.DataStale);
        Assert.False(viewModel.HasOperationWarning);
        Assert.Equal(string.Empty, viewModel.OperationWarning);
    }

    private static CipherListItem Item(string id, bool deleted) => new()
    {
        Id = id,
        Name = id,
        IsDeleted = deleted,
        SearchHaystack = id,
    };

    private static void AssertEmptyStates(
        VaultViewModel viewModel,
        bool vaultEmpty,
        bool noResults,
        bool trashEmpty)
    {
        Assert.Equal(vaultEmpty, viewModel.HasNoItems);
        Assert.Equal(noResults, viewModel.NoResults);
        Assert.Equal(trashEmpty, viewModel.IsTrashEmpty);
        Assert.InRange(
            new[] { viewModel.HasNoItems, viewModel.NoResults, viewModel.IsTrashEmpty }.Count(value => value),
            0,
            1);
    }

    private sealed class MutableStateVaultUiService : IVaultUiService
    {
        private readonly MockVaultUiService _inner = new();

        public IReadOnlyList<CipherListItem>? ItemsOverride { get; set; }
        public int SkippedCipherCount { get; set; }
        public bool DataStale { get; set; }
        public Action? SyncMutation { get; set; }
        public Exception? SaveException { get; set; }
        public int SaveCalls { get; private set; }

        public IReadOnlyList<CipherListItem> GetItems() => ItemsOverride ?? _inner.GetItems();

        public CipherAccessPolicy GetAccessPolicy(string id) => _inner.GetAccessPolicy(id);

        public CipherDetail GetDetail(string id, bool includeSensitive) =>
            _inner.GetDetail(id, includeSensitive);

        public IReadOnlyList<FilterNode> GetFilters()
        {
            var items = GetItems();
            return
            [
                new FilterNode
                {
                    Label = "All items",
                    Kind = FilterKind.AllItems,
                    Count = items.Count(item => !item.IsDeleted),
                },
                new FilterNode
                {
                    Label = "Trash",
                    Kind = FilterKind.Trash,
                    Count = items.Count(item => item.IsDeleted),
                },
            ];
        }

        public CipherEditorDraft GetDraft(string id) => _inner.GetDraft(id);

        public Task<string> SaveCipherAsync(
            CipherEditorDraft draft,
            string? editingId,
            CancellationToken ct = default)
        {
            SaveCalls++;
            return SaveException is null
                ? _inner.SaveCipherAsync(draft, editingId, ct)
                : Task.FromException<string>(SaveException);
        }

        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) =>
            _inner.DeleteCipherAsync(id, permanent, ct);

        public Task RestoreCipherAsync(string id, CancellationToken ct = default) =>
            _inner.RestoreCipherAsync(id, ct);

        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) =>
            _inner.SaveFolderAsync(folderId, name, ct);

        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
            _inner.DeleteFolderAsync(folderId, ct);

        public Task SyncAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SyncMutation?.Invoke();
            return Task.CompletedTask;
        }

        public Task MoveCiphersAsync(
            IReadOnlyCollection<string> ids,
            string? folderId,
            CancellationToken ct = default) =>
            _inner.MoveCiphersAsync(ids, folderId, ct);

        public Task DeleteCiphersAsync(
            IReadOnlyCollection<string> ids,
            bool permanent,
            CancellationToken ct = default) =>
            _inner.DeleteCiphersAsync(ids, permanent, ct);

        public Task RestoreCiphersAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken ct = default) =>
            _inner.RestoreCiphersAsync(ids, ct);
    }
}
