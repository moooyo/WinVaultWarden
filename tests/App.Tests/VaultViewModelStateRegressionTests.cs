using System.Collections.Specialized;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
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
    public async Task SaveDraft_CommittedRefreshFailure_RefreshesRevokedCapabilitiesAndListActions()
    {
        var service = new MutableStateVaultUiService
        {
            Capabilities = new Core.Models.VaultCapabilities(
                CanWriteNewCipherTypes: false,
                SupportsArchiveProtocol: true,
                HasArchiveEntitlement: true),
            SaveException = new Core.Services.VaultRefreshException(
                "Remote write committed.",
                new HttpRequestException("Refresh failed.")),
        };
        service.SaveMutation = () => service.Capabilities = Core.Models.VaultCapabilities.None;
        var viewModel = new VaultViewModel(service);
        Assert.True(viewModel.CanArchiveItems);
        Assert.Contains(viewModel.Items, item => item.ShowArchiveAction);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Committed create";

        var result = await viewModel.SaveDraftAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(Core.Models.VaultCapabilities.None, viewModel.Capabilities);
        Assert.False(viewModel.CanArchiveItems);
        Assert.False(viewModel.CanUnarchiveItems);
        Assert.False(viewModel.ShowArchiveSelectionAction);
        Assert.All(viewModel.Items, item =>
        {
            Assert.False(item.ShowArchiveAction);
            Assert.False(item.ShowUnarchiveAction);
        });
        Assert.True(viewModel.DataStale);
        Assert.True(viewModel.HasOperationWarning);
        Assert.False(viewModel.IsEditing);
        Assert.Null(viewModel.EditorDraft);
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

    [Fact]
    public async Task SaveDraft_RawConfirmedCreateMissingFromVisibleItemsWarnsAndCannotBeResubmitted()
    {
        var service = new MutableStateVaultUiService
        {
            ItemsOverride = [],
            SkippedCipherCount = 1,
            UnsupportedOrganizationCipherCount = 2,
        };
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Confirmed but hidden";

        var result = await viewModel.SaveDraftAsync();

        Assert.True(result.IsSuccess);
        Assert.False(viewModel.IsEditing);
        Assert.Null(viewModel.EditorDraft);
        Assert.Null(viewModel.SelectedItem);
        Assert.Equal(1, viewModel.SkippedCipherCount);
        Assert.Equal(2, viewModel.UnsupportedOrganizationCipherCount);
        Assert.Contains("已确认保存", viewModel.OperationWarning);
        Assert.Contains("请勿重新创建", viewModel.OperationWarning);

        var repeated = await viewModel.SaveDraftAsync();
        Assert.False(repeated.IsSuccess);
        Assert.Equal(1, service.SaveCalls);
    }

    [Fact]
    public async Task SaveDraft_UncertainCreate_PreservesAndBlocksDraftUntilReconciled()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = new MutableStateVaultUiService
        {
            SaveException = new CipherCreateOutcomeUncertainException(
                "Create outcome is uncertain.",
                pending,
                new HttpRequestException("Mutation response was unavailable."),
                new HttpRequestException("Refresh failed.")),
        };
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Uncertain create";
        var draft = viewModel.EditorDraft;

        var result = await viewModel.SaveDraftAsync();

        Assert.False(result.IsSuccess);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.True(viewModel.IsEditing);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.True(viewModel.DataStale);
        Assert.True(viewModel.HasOperationWarning);
        Assert.True(viewModel.HasOperationError);
        Assert.Equal("保存结果待确认", viewModel.OperationWarningTitle);
        Assert.False(viewModel.CanSaveEditorDraft);
        Assert.False(viewModel.CanCancelEditor);
        Assert.False(viewModel.CanEditEditorDraft);
        Assert.False(viewModel.CanBeginCipherCreate);

        viewModel.CancelEdit();
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.True(viewModel.IsEditing);

        viewModel.BeginAdd(VaultItemKind.Card);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.Equal(VaultItemKind.Login, viewModel.EditorDraft!.Type);

        var repeated = await viewModel.SaveDraftAsync();
        Assert.False(repeated.IsSuccess);
        Assert.Equal(1, service.SaveCalls);
    }

    [Fact]
    public async Task SaveDraft_SnapshotMismatch_PreservesAndBlocksDraft()
    {
        var pending = CreatePending(commitConfirmed: true, expectedCipherId: "expected-id");
        var mismatch = new CipherCreateReconciliationResult(
            CipherCreateReconciliationStatus.SnapshotMismatch,
            null,
            []);
        var service = new MutableStateVaultUiService
        {
            SaveException = new CipherCreateSnapshotMismatchException(
                "Create snapshot could not be reconciled.",
                pending,
                mismatch),
        };
        service.SaveMutation = () =>
            service.ItemsOverride = [Item("authoritative-item", deleted: false)];
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Confirmed but missing";
        var draft = viewModel.EditorDraft;

        var result = await viewModel.SaveDraftAsync();

        Assert.False(result.IsSuccess);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.True(viewModel.DataStale);
        Assert.False(viewModel.CanSaveEditorDraft);
        Assert.False(viewModel.CanCancelEditor);
        Assert.True(viewModel.HasOperationWarning);
        Assert.True(viewModel.HasOperationError);
        Assert.Equal("authoritative-item", Assert.Single(viewModel.Items).Id);
    }

    [Fact]
    public async Task SyncAfterUncertainCreate_Confirmed_ClosesEditorAndSelectsCreatedCipher()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = PendingCreateService(pending);
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Confirmed after sync";
        await viewModel.SaveDraftAsync();
        service.SaveException = null;
        service.Reconciliation = new CipherCreateReconciliationResult(
            CipherCreateReconciliationStatus.Confirmed,
            "created-after-sync",
            ["created-after-sync"]);
        service.SyncMutation = () =>
        {
            service.DataStale = false;
            service.ItemsOverride = [Item("created-after-sync", deleted: false)];
        };

        var result = await viewModel.SyncAsync();

        Assert.True(result.IsSuccess);
        Assert.Same(pending, service.LastReconciledPending);
        Assert.Equal(1, service.ReconcileCalls);
        Assert.False(viewModel.HasPendingCipherCreate);
        Assert.False(viewModel.IsEditing);
        Assert.Null(viewModel.EditorDraft);
        Assert.Equal("created-after-sync", viewModel.SelectedItem?.Id);
        Assert.Equal("created-after-sync", viewModel.Detail?.Id);
        Assert.False(viewModel.HasOperationWarning);
        Assert.False(viewModel.HasOperationError);
    }

    [Fact]
    public async Task SyncAfterUncertainCreate_NotFoundWithoutCommitConfirmation_RemainsBlocked()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = PendingCreateService(pending);
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Safe to retry";
        var draft = viewModel.EditorDraft;
        await viewModel.SaveDraftAsync();
        service.SaveException = null;
        service.DataStale = false;
        service.Reconciliation = new CipherCreateReconciliationResult(
            CipherCreateReconciliationStatus.NotFound,
            null,
            []);

        var result = await viewModel.SyncAsync();

        Assert.True(result.IsSuccess);
        Assert.Same(pending, service.LastReconciledPending);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.True(viewModel.IsEditing);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.False(viewModel.CanSaveEditorDraft);
        Assert.False(viewModel.CanCancelEditor);
        Assert.True(viewModel.DataStale);
        Assert.True(viewModel.HasOperationWarning);
        Assert.True(viewModel.HasOperationError);
    }

    [Fact]
    public async Task AbandonPendingCreate_OwnedDraftIsPreservedAndNeverResubmitted()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = PendingCreateService(pending);
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Review before retry";
        var draft = viewModel.EditorDraft;
        await viewModel.SaveDraftAsync();
        var expected = Assert.IsType<CipherCreatePending>(viewModel.CapturePendingCipherCreate());

        var result = await viewModel.AbandonPendingCipherCreateAsync(expected);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, service.AbandonCalls);
        Assert.Equal(1, service.SaveCalls);
        Assert.False(viewModel.HasPendingCipherCreate);
        Assert.True(viewModel.IsEditing);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.True(viewModel.CanEditEditorDraft);
        Assert.True(viewModel.CanCancelEditor);
        Assert.True(viewModel.CanSaveEditorDraft);
        Assert.Equal("待确认状态已放弃", viewModel.OperationWarningTitle);
        Assert.Contains("服务器上仍可能存在", viewModel.OperationWarning);
    }

    [Fact]
    public async Task AbandonPendingCreate_WithoutLocalDraftClearsGateWithoutCreatingOrSavingDraft()
    {
        var service = new MutableStateVaultUiService();
        service.RememberPendingCipherCreate(CreatePending(commitConfirmed: true, "server-id"));
        var viewModel = new VaultViewModel(service);
        var expected = Assert.IsType<CipherCreatePending>(viewModel.CapturePendingCipherCreate());

        var result = await viewModel.AbandonPendingCipherCreateAsync(expected);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, service.AbandonCalls);
        Assert.Equal(0, service.SaveCalls);
        Assert.False(viewModel.HasPendingCipherCreate);
        Assert.False(viewModel.IsEditing);
        Assert.Null(viewModel.EditorDraft);
        Assert.True(viewModel.CanBeginCipherCreate);
    }

    [Fact]
    public async Task NonOwningPendingCreate_DoesNotFreezeOrCloseExistingCipherDraft()
    {
        var service = new MutableStateVaultUiService();
        var viewModel = new VaultViewModel(service);
        var edit = await viewModel.BeginEditAsync("1", _ => Task.FromResult<string?>(null));
        Assert.True(edit.IsSuccess);
        viewModel.EditorDraft!.Name = "Unrelated update";
        var draft = viewModel.EditorDraft;
        service.RememberPendingCipherCreate(CreatePending(commitConfirmed: false));

        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.False(viewModel.HasRecoverablePendingDraft);
        Assert.True(viewModel.CanEditEditorDraft);
        Assert.True(viewModel.CanCancelEditor);
        Assert.True(viewModel.CanSaveEditorDraft);
        var expected = Assert.IsType<CipherCreatePending>(viewModel.CapturePendingCipherCreate());

        var result = await viewModel.AbandonPendingCipherCreateAsync(expected);

        Assert.True(result.IsSuccess);
        Assert.True(viewModel.IsEditing);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.Equal("Unrelated update", viewModel.EditorDraft!.Name);
        Assert.True(viewModel.CanEditEditorDraft);
        Assert.True(viewModel.CanCancelEditor);
        Assert.True(viewModel.CanSaveEditorDraft);
        Assert.Equal(0, service.SaveCalls);
    }

    [Fact]
    public async Task AbandonPendingCreate_StaleCapturedStateCannotClearUpdatedLedger()
    {
        var service = new MutableStateVaultUiService();
        service.RememberPendingCipherCreate(CreatePending(commitConfirmed: false));
        var viewModel = new VaultViewModel(service);
        var stale = Assert.IsType<CipherCreatePending>(viewModel.CapturePendingCipherCreate());
        service.ReplacePendingCipherCreate(stale with
        {
            CommitConfirmed = true,
            ExpectedCipherId = "server-id",
        });

        var result = await viewModel.AbandonPendingCipherCreateAsync(stale);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, service.AbandonCalls);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.Contains("服务器曾返回成功", viewModel.PendingCipherCreateMessage);
    }

    [Fact]
    public async Task AbandonPendingCreate_LedgerDeleteFailureKeepsOwnedDraftFrozen()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = PendingCreateService(pending);
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Keep blocked";
        var draft = viewModel.EditorDraft;
        await viewModel.SaveDraftAsync();
        service.AbandonException = new IOException("ledger unavailable");
        var expected = Assert.IsType<CipherCreatePending>(viewModel.CapturePendingCipherCreate());

        var result = await viewModel.AbandonPendingCipherCreateAsync(expected);

        Assert.False(result.IsSuccess);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.True(viewModel.IsEditing);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.False(viewModel.CanEditEditorDraft);
        Assert.False(viewModel.CanCancelEditor);
        Assert.False(viewModel.CanSaveEditorDraft);
        Assert.Contains("防重复保护仍然有效", viewModel.OperationError);
        Assert.Equal(1, service.SaveCalls);
    }

    [Theory]
    [InlineData(CipherCreateReconciliationStatus.NotFound, true)]
    [InlineData(CipherCreateReconciliationStatus.Ambiguous, false)]
    [InlineData(CipherCreateReconciliationStatus.SnapshotMismatch, false)]
    [InlineData(CipherCreateReconciliationStatus.SessionMismatch, false)]
    public async Task SyncAfterUncertainCreate_UnresolvedResult_KeepsDraftBlocked(
        CipherCreateReconciliationStatus status,
        bool commitConfirmed)
    {
        var pending = CreatePending(commitConfirmed, commitConfirmed ? "expected-id" : null);
        var service = PendingCreateService(pending);
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Still uncertain";
        var draft = viewModel.EditorDraft;
        await viewModel.SaveDraftAsync();
        service.SaveException = null;
        service.DataStale = false;
        service.Reconciliation = new CipherCreateReconciliationResult(
            status,
            null,
            status == CipherCreateReconciliationStatus.Ambiguous ? ["a", "b"] : []);

        var result = await viewModel.SyncAsync();

        Assert.True(result.IsSuccess);
        Assert.Same(pending, service.LastReconciledPending);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.True(viewModel.IsEditing);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.False(viewModel.CanSaveEditorDraft);
        Assert.False(viewModel.CanCancelEditor);
        Assert.True(viewModel.DataStale);
        Assert.True(viewModel.HasOperationWarning);
        Assert.True(viewModel.HasOperationError);
    }

    [Fact]
    public async Task SyncAfterUncertainCreate_SyncFailure_KeepsPendingWithoutReconciliation()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = PendingCreateService(pending);
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Sync failed";
        var draft = viewModel.EditorDraft;
        await viewModel.SaveDraftAsync();
        service.SaveException = null;
        service.SyncException = new HttpRequestException("Offline.");

        var result = await viewModel.SyncAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(0, service.ReconcileCalls);
        Assert.Null(service.LastReconciledPending);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.Same(draft, viewModel.EditorDraft);
        Assert.False(viewModel.CanSaveEditorDraft);
        Assert.False(viewModel.CanCancelEditor);
        Assert.True(viewModel.DataStale);
        Assert.True(viewModel.HasOperationWarning);
    }

    [Fact]
    public async Task PendingCreate_IsRestoredIntoANewViewModelAndBlocksAnotherCreate()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = PendingCreateService(pending);
        var first = new VaultViewModel(service);
        first.BeginAdd(VaultItemKind.Login);
        first.EditorDraft!.Name = "Uncertain create";
        await first.SaveDraftAsync();

        var replacement = new VaultViewModel(service);

        Assert.True(replacement.HasPendingCipherCreate);
        Assert.False(replacement.CanBeginCipherCreate);
        replacement.BeginAdd(VaultItemKind.Card);
        Assert.False(replacement.IsEditing);
        Assert.Null(replacement.EditorDraft);
        Assert.Contains("上一次新建结果", replacement.OperationError);
    }

    [Fact]
    public async Task LateUncertainCreateAfterClearSensitiveIsRememberedButNotPublishedIntoOldViewModel()
    {
        var pending = CreatePending(commitConfirmed: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = PendingCreateService(pending);
        service.SaveEntered = entered;
        service.SaveRelease = release;
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Login);
        viewModel.EditorDraft!.Name = "Slow create";

        var save = viewModel.SaveDraftAsync();
        await entered.Task;
        var replacement = new VaultViewModel(service);
        Assert.False(replacement.HasPendingCipherCreate);
        viewModel.ClearSensitive();
        release.SetResult();
        var result = await save;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.False(viewModel.HasPendingCipherCreate);
        Assert.Null(viewModel.EditorDraft);
        Assert.False(viewModel.HasOperationWarning);
        Assert.Same(pending, service.GetPendingCipherCreate());

        replacement.BeginAdd(VaultItemKind.Card);
        Assert.True(replacement.HasPendingCipherCreate);
        Assert.False(replacement.IsEditing);
        Assert.Null(replacement.EditorDraft);
    }

    [Fact]
    public async Task ReconcileFromAnotherViewModel_ClosesOnlyTheDraftThatOwnsThePendingCreate()
    {
        var pending = CreatePending(commitConfirmed: false);
        var service = PendingCreateService(pending);
        var creator = new VaultViewModel(service);
        var editor = new VaultViewModel(service);
        var cancellableEditor = new VaultViewModel(service);
        var savingEditor = new VaultViewModel(service);
        creator.BeginAdd(VaultItemKind.Login);
        creator.EditorDraft!.Name = "Uncertain create";
        var editResult = await editor.BeginEditAsync(
            "1",
            _ => Task.FromResult<string?>(null));
        var cancellableEditResult = await cancellableEditor.BeginEditAsync(
            "1",
            _ => Task.FromResult<string?>(null));
        var savingEditResult = await savingEditor.BeginEditAsync(
            "1",
            _ => Task.FromResult<string?>(null));
        Assert.True(editResult.IsSuccess);
        Assert.True(cancellableEditResult.IsSuccess);
        Assert.True(savingEditResult.IsSuccess);
        editor.EditorDraft!.Name = "Unrelated edit";
        savingEditor.EditorDraft!.Name = "Safe existing update";
        var unrelatedDraft = editor.EditorDraft;

        await creator.SaveDraftAsync();
        Assert.True(creator.HasPendingCipherCreate);
        Assert.True(editor.HasPendingCipherCreate);
        Assert.Same(unrelatedDraft, editor.EditorDraft);
        Assert.True(editor.CanEditEditorDraft);
        Assert.True(editor.CanCancelEditor);
        Assert.True(editor.CanSaveEditorDraft);
        Assert.True(cancellableEditor.CanCancelEditor);
        cancellableEditor.CancelEdit();
        Assert.False(cancellableEditor.IsEditing);
        Assert.Null(cancellableEditor.EditorDraft);

        service.SaveException = null;
        var existingSave = await savingEditor.SaveDraftAsync();
        Assert.True(existingSave.IsSuccess);
        Assert.False(savingEditor.IsEditing);
        Assert.Null(savingEditor.EditorDraft);
        service.SyncMutation = () => service.ItemsOverride =
        [
            Item("1", deleted: false),
            Item("created-id", deleted: false),
        ];
        service.Reconciliation = new CipherCreateReconciliationResult(
            CipherCreateReconciliationStatus.Confirmed,
            "created-id",
            ["created-id"]);

        var reconciliation = await editor.SyncAsync();

        Assert.True(reconciliation.IsSuccess);
        Assert.False(creator.IsEditing);
        Assert.Null(creator.EditorDraft);
        Assert.False(creator.HasPendingCipherCreate);
        Assert.True(editor.IsEditing);
        Assert.Same(unrelatedDraft, editor.EditorDraft);
        Assert.Equal("Unrelated edit", editor.EditorDraft!.Name);
        Assert.False(editor.HasPendingCipherCreate);
    }

    [Fact]
    public async Task PendingEventDuringARejectedCreate_DoesNotClaimTheOtherRequestsDraft()
    {
        var pending = CreatePending(commitConfirmed: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MutableStateVaultUiService
        {
            SaveEntered = entered,
            SaveRelease = release,
            SaveException = new CipherCreatePendingOperationException(),
        };
        var viewModel = new VaultViewModel(service);
        viewModel.BeginAdd(VaultItemKind.Card);
        viewModel.EditorDraft!.Name = "Other request";
        var unrelatedDraft = viewModel.EditorDraft;

        var rejectedSave = viewModel.SaveDraftAsync();
        await entered.Task;
        service.RememberPendingCipherCreate(pending);
        release.SetResult();
        var rejected = await rejectedSave;

        Assert.False(rejected.IsSuccess);
        Assert.True(viewModel.HasPendingCipherCreate);
        Assert.Same(unrelatedDraft, viewModel.EditorDraft);
        Assert.True(viewModel.CanEditEditorDraft);
        Assert.True(viewModel.CanCancelEditor);
        Assert.False(viewModel.CanSaveEditorDraft);

        service.SaveException = null;
        service.Reconciliation = new CipherCreateReconciliationResult(
            CipherCreateReconciliationStatus.Confirmed,
            "1",
            ["1"]);

        var reconciled = await viewModel.SyncAsync();

        Assert.True(reconciled.IsSuccess);
        Assert.True(viewModel.IsEditing);
        Assert.Same(unrelatedDraft, viewModel.EditorDraft);
        Assert.Equal("Other request", viewModel.EditorDraft!.Name);
        Assert.False(viewModel.HasPendingCipherCreate);
    }

    private static MutableStateVaultUiService PendingCreateService(CipherCreatePending pending) =>
        new()
        {
            SaveException = new CipherCreateOutcomeUncertainException(
                "Create outcome is uncertain.",
                pending,
                null,
                new HttpRequestException("Refresh failed.")),
        };

    private static CipherCreatePending CreatePending(
        bool commitConfirmed,
        string? expectedCipherId = null) =>
        new(
            new CipherCreateWitness(
                "account-1",
                ServerEndpoints.BitwardenUs,
                CipherType.Login,
                null,
                [],
                ["existing-id"],
                "A1B2C3"),
            commitConfirmed,
            expectedCipherId);

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
        public Core.Models.VaultCapabilities Capabilities { get; set; } =
            Core.Models.VaultCapabilities.None;
        public int SkippedCipherCount { get; set; }
        public int UnsupportedOrganizationCipherCount { get; set; }
        public bool DataStale { get; set; }
        public Action? SyncMutation { get; set; }
        public Action? SaveMutation { get; set; }
        public Exception? SaveException { get; set; }
        public Exception? SyncException { get; set; }
        public Exception? AbandonException { get; set; }
        public CipherCreateReconciliationResult? Reconciliation { get; set; }
        public CipherCreatePending? LastReconciledPending { get; private set; }
        public CipherCreatePending? PendingCreate { get; private set; }
        public int SaveCalls { get; private set; }
        public int ReconcileCalls { get; private set; }
        public int AbandonCalls { get; private set; }
        public TaskCompletionSource? SaveEntered { get; set; }
        public TaskCompletionSource? SaveRelease { get; set; }
        public event EventHandler? CipherCreateGateChanged;

        public IReadOnlyList<CipherListItem> GetItems()
        {
            _inner.Capabilities = Capabilities;
            return ItemsOverride ?? _inner.GetItems();
        }

        public CipherAccessPolicy GetAccessPolicy(string id) =>
            ItemsOverride?.Any(item => item.Id == id) == true
                ? new CipherAccessPolicy(id, false)
                : _inner.GetAccessPolicy(id);

        public CipherDetail GetDetail(string id, bool includeSensitive) =>
            ItemsOverride?.FirstOrDefault(item => item.Id == id) is { } item
                ? new LoginDetail
                {
                    Id = item.Id,
                    Name = item.Name,
                    SensitiveUnlocked = includeSensitive,
                }
                : _inner.GetDetail(id, includeSensitive);

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

        public async Task<string> SaveCipherAsync(
            CipherEditorDraft draft,
            string? editingId,
            CancellationToken ct = default)
        {
            SaveCalls++;
            SaveMutation?.Invoke();
            SaveEntered?.TrySetResult();
            if (SaveRelease is not null)
                await SaveRelease.Task;
            if (SaveException is not null)
                throw SaveException;
            return await _inner.SaveCipherAsync(draft, editingId, ct);
        }

        public CipherCreatePending? GetPendingCipherCreate() => PendingCreate;

        public CipherCreatePending RememberPendingCipherCreate(CipherCreatePending pending)
        {
            PendingCreate ??= pending;
            CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
            return PendingCreate;
        }

        public void ReplacePendingCipherCreate(CipherCreatePending pending)
        {
            PendingCreate = pending;
            CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool AbandonPendingCipherCreate(CipherCreatePending expected)
        {
            AbandonCalls++;
            if (AbandonException is not null)
                throw AbandonException;
            if (PendingCreate is null || !PendingStateMatches(PendingCreate, expected))
                return false;

            PendingCreate = null;
            CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public async Task<CipherCreateReconciliationResult> SyncAndReconcileCipherCreateAsync(
            CipherCreatePending pending,
            CancellationToken ct = default)
        {
            await SyncAsync(ct);
            ReconcileCalls++;
            LastReconciledPending = pending;
            var result = Reconciliation
                ?? throw new InvalidOperationException("No create reconciliation was configured.");
            if (result.Status == CipherCreateReconciliationStatus.Confirmed)
            {
                PendingCreate = null;
                CipherCreateGateChanged?.Invoke(this, EventArgs.Empty);
            }
            return result;
        }

        private static bool PendingStateMatches(
            CipherCreatePending left,
            CipherCreatePending right) =>
            left.Witness.AccountId == right.Witness.AccountId
            && left.Witness.Endpoints == right.Witness.Endpoints
            && left.Witness.KeyFingerprint == right.Witness.KeyFingerprint
            && left.CommitConfirmed == right.CommitConfirmed
            && left.ExpectedCipherId == right.ExpectedCipherId;

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
            if (SyncException is not null)
                return Task.FromException(SyncException);
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
