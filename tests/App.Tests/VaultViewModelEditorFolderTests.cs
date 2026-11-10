using System.Net;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class VaultViewModelEditorFolderTests
{
    [Fact]
    public async Task CreateFolderPreservesUnsavedDraftAndWorkspace()
    {
        var service = new EditorFolderService();
        var vm = new VaultViewModel(service);
        vm.SelectFilterByTag("vault:folder:f1");
        vm.SearchText = "百度";
        vm.SelectedItem = vm.Items.Single(item => item.Id == "1");
        Assert.True((await vm.BeginEditAsync("1", _ => Task.FromResult<string?>(null))).IsSuccess);
        var draft = vm.EditorDraft!;
        draft.Name = "Unsaved item";
        draft.Login.Password = "Unsaved password";
        draft.Notes = "Unsaved notes";
        draft.CustomFields.Add(new CustomFieldEditorDraft { Name = "Draft field", Value = "Draft value" });
        var raised = 0;
        vm.FoldersChanged += (_, _) => raised++;

        var result = await vm.CreateFolderForEditorAsync(draft, "  New folder  ");

        Assert.True(result.IsSuccess);
        Assert.True(vm.IsEditing);
        Assert.Same(draft, vm.EditorDraft);
        Assert.True(vm.HasUnsavedEditorChanges);
        Assert.Equal("Unsaved item", draft.Name);
        Assert.Equal("Unsaved password", draft.Login.Password);
        Assert.Equal("Unsaved notes", draft.Notes);
        Assert.Contains(draft.CustomFields, field => field.Name == "Draft field" && field.Value == "Draft value");
        Assert.Equal(service.LastCreatedId, draft.FolderId);
        Assert.Equal("New folder", vm.EditorFolderLabel);
        Assert.Equal("vault:folder:f1", vm.SelectedFilterTag);
        Assert.Equal("百度", vm.SearchText);
        Assert.Equal("1", vm.SelectedItem?.Id);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ConcurrentDuplicateNamesUseOnlyTheReturnedCreationId()
    {
        var service = new EditorFolderService();
        service.Create = async (name, ct) =>
        {
            await service.CreateDirectlyAsync(name, ct);
            return await service.CreateDirectlyAsync(name, ct);
        };
        var vm = new VaultViewModel(service);
        vm.BeginAdd(VaultItemKind.Login);
        var draft = vm.EditorDraft!;

        var result = await vm.CreateFolderForEditorAsync(draft, "Duplicate");

        Assert.True(result.IsSuccess);
        Assert.Equal(2, vm.FolderFilters.Count(folder => folder.Label == "Duplicate"));
        Assert.Equal(service.LastCreatedId, draft.FolderId);
    }

    [Fact]
    public async Task FolderCreationUsesTheVaultBusyGateAndKeepsTheEditorOpen()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new EditorFolderService();
        service.Create = async (name, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return await service.CreateDirectlyAsync(name, ct);
        };
        var vm = new VaultViewModel(service);
        vm.BeginAdd(VaultItemKind.Login);
        var draft = vm.EditorDraft!;

        var creation = vm.CreateFolderForEditorAsync(draft, "First");
        Assert.Equal(VaultOperationKind.Folder, vm.CurrentOperation);
        Assert.False(vm.CanEditEditorDraft);
        Assert.False(vm.CanCancelEditor);
        vm.CancelEdit();
        var conflicting = await vm.CreateFolderForEditorAsync(draft, "Second");
        Assert.Equal(UiOperationStatus.Busy, conflicting.Status);
        Assert.Equal(1, service.CreateCalls);
        Assert.Same(draft, vm.EditorDraft);

        release.SetResult();
        Assert.True((await creation).IsSuccess);
        Assert.True(vm.IsEditing);
        Assert.True(vm.CanEditEditorDraft);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CancelledRequestDoesNotCreateAFolder()
    {
        var service = new EditorFolderService();
        var vm = new VaultViewModel(service);
        vm.BeginAdd(VaultItemKind.Login);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await vm.CreateFolderForEditorAsync(vm.EditorDraft!, "Cancelled", cancellation.Token);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(0, service.CreateCalls);
        Assert.True(vm.IsEditing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCreationCannotRestoreOrModifyAReplacedDraft(bool clearSensitive)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new EditorFolderService();
        service.Create = async (name, _) =>
        {
            await release.Task;
            return await service.CreateDirectlyAsync(name);
        };
        var vm = new VaultViewModel(service);
        vm.BeginAdd(VaultItemKind.Login);
        var draft = vm.EditorDraft!;
        draft.FolderId = "f1";
        var raised = 0;
        vm.FoldersChanged += (_, _) => raised++;
        var creation = vm.CreateFolderForEditorAsync(draft, "Late folder");

        CipherEditorDraft? replacement = null;
        if (clearSensitive)
            vm.ClearSensitive();
        else
            vm.EditorDraft = replacement = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        release.SetResult();
        var result = await creation;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Same(replacement, vm.EditorDraft);
        Assert.Equal("f1", draft.FolderId);
        Assert.Null(replacement?.FolderId);
        Assert.DoesNotContain(vm.FolderFilters, folder => folder.Label == "Late folder");
        Assert.Equal(0, raised);
        Assert.Empty(vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task UnreadableSuccessfulResponsePreservesDraftAndRequiresSyncWithoutRetry()
    {
        var service = new EditorFolderService
        {
            Create = (_, _) => throw new Api.FolderCreateSuccessResponseReadException(
                HttpStatusCode.OK, new InvalidDataException("Invalid folder response.")),
        };
        var vm = new VaultViewModel(service);
        vm.BeginAdd(VaultItemKind.Login);
        var draft = vm.EditorDraft!;
        draft.FolderId = "f1";

        var result = await vm.CreateFolderForEditorAsync(draft, "Uncertain folder");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(1, service.CreateCalls);
        Assert.Same(draft, vm.EditorDraft);
        Assert.Equal("f1", draft.FolderId);
        Assert.True(vm.DataStale);
        Assert.Contains("避免重复新建", vm.OperationWarning);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshFailurePreservesDraftAndDoesNotGuessAFolderId()
    {
        var service = new EditorFolderService();
        service.Create = async (name, ct) =>
        {
            await service.CreateDirectlyAsync(name, ct);
            throw new VaultRefreshException("Refresh failed.", new IOException("Unavailable snapshot."));
        };
        var vm = new VaultViewModel(service);
        vm.BeginAdd(VaultItemKind.Login);
        var draft = vm.EditorDraft!;
        draft.FolderId = "f1";

        var result = await vm.CreateFolderForEditorAsync(draft, "Committed folder");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, service.CreateCalls);
        Assert.Same(draft, vm.EditorDraft);
        Assert.Equal("f1", draft.FolderId);
        Assert.Contains(vm.FolderFilters, folder => folder.FolderId == service.LastCreatedId);
        Assert.True(vm.DataStale);
        Assert.Contains("文件夹已创建", vm.OperationWarning);
        Assert.False(vm.IsBusy);
    }

    private sealed class EditorFolderService : IVaultUiService
    {
        private readonly MockVaultUiService _inner = new();
        public Func<string, CancellationToken, Task<string>>? Create { get; set; }
        public int CreateCalls { get; private set; }
        public string? LastCreatedId { get; private set; }
        public int SkippedCipherCount => _inner.SkippedCipherCount;
        public bool DataStale => _inner.DataStale;
        public IReadOnlyList<CipherListItem> GetItems() => _inner.GetItems();
        public CipherAccessPolicy GetAccessPolicy(string id) => _inner.GetAccessPolicy(id);
        public CipherDetail GetDetail(string id, bool includeSensitive) => _inner.GetDetail(id, includeSensitive);
        public IReadOnlyList<FilterNode> GetFilters() => _inner.GetFilters();
        public CipherEditorDraft GetDraft(string id) => _inner.GetDraft(id);
        public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default) =>
            _inner.SaveCipherAsync(draft, editingId, ct);
        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) =>
            _inner.DeleteCipherAsync(id, permanent, ct);
        public Task RestoreCipherAsync(string id, CancellationToken ct = default) => _inner.RestoreCipherAsync(id, ct);
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) =>
            _inner.SaveFolderAsync(folderId, name, ct);
        public Task<string> CreateFolderAndGetIdAsync(string name, CancellationToken ct = default)
        {
            CreateCalls++;
            return Create is null ? CreateDirectlyAsync(name, ct) : Create(name, ct);
        }
        public async Task<string> CreateDirectlyAsync(string name, CancellationToken ct = default)
        {
            LastCreatedId = await _inner.CreateFolderAndGetIdAsync(name, ct);
            return LastCreatedId;
        }
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => _inner.DeleteFolderAsync(folderId, ct);
        public Task SyncAsync(CancellationToken ct = default) => _inner.SyncAsync(ct);
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) =>
            _inner.MoveCiphersAsync(ids, folderId, ct);
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) =>
            _inner.DeleteCiphersAsync(ids, permanent, ct);
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            _inner.RestoreCiphersAsync(ids, ct);
    }
}
