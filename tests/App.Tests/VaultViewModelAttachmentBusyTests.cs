using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Models;
using Xunit;

namespace App.Tests;

// Task 11:附件上传/下载/删除与其余 Vault 写入/同步操作共用同一门禁(_operationGate)。
// 本文件验证两个方向:1) 附件下载在途时保存条目返回 Busy;2) 附件上传在途时同步返回 Busy。
// 两个方向都断言:第二次调用返回 Busy、底层服务调用数保持不变(无副作用),完成后门禁释放。
public class VaultViewModelAttachmentBusyTests
{
    // 门控 fake:附件操作在被显式释放(SetResult)前保持挂起,以便断言"进行中"占用门禁。
    private sealed class GatedAttachmentUiService : IAttachmentUiService
    {
        public TaskCompletionSource<bool> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AddCalls { get; private set; }
        public int DownloadCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool ThrowOnRelease { get; set; }

        public async Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(
            string cipherId,
            ReopenableFileSource file,
            CancellationToken ct = default)
        {
            AddCalls++;
            await Gate.Task;
            if (ThrowOnRelease)
                throw new HttpRequestException("upload failed");
            return Array.Empty<AttachmentItem>();
        }

        public async Task<long> DownloadAttachmentAsync(
            string cipherId,
            string attachmentId,
            Stream plaintextDestination,
            CancellationToken ct = default)
        {
            DownloadCalls++;
            await Gate.Task;
            if (ThrowOnRelease)
                throw new HttpRequestException("download failed");
            return 0L;
        }

        public async Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        {
            DeleteCalls++;
            await Gate.Task;
            if (ThrowOnRelease)
                throw new HttpRequestException("delete failed");
            return Array.Empty<AttachmentItem>();
        }
    }

    // 门控 fake:SaveCipherAsync/SyncAsync 在被显式释放前保持挂起,用于反方向验证(附件操作
    // 在保存/同步进行中被阻塞)。
    private sealed class GatedVaultUiService : IVaultUiService
    {
        private readonly MockVaultUiService _inner = new();
        public TaskCompletionSource<string> SaveGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SyncGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SaveCalls { get; private set; }
        public int SyncCalls { get; private set; }
        public int SkippedCipherCount => _inner.SkippedCipherCount;
        public bool DataStale => _inner.DataStale;
        public IReadOnlyList<CipherListItem> GetItems() => _inner.GetItems();
        public CipherAccessPolicy GetAccessPolicy(string id) => _inner.GetAccessPolicy(id);
        public CipherDetail GetDetail(string id, bool includeSensitive) => _inner.GetDetail(id, includeSensitive);
        public IReadOnlyList<FilterNode> GetFilters() => _inner.GetFilters();
        public CipherEditorDraft GetDraft(string id) => _inner.GetDraft(id);
        public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default)
        {
            SaveCalls++;
            return SaveGate.Task;
        }
        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => _inner.DeleteCipherAsync(id, permanent, ct);
        public Task RestoreCipherAsync(string id, CancellationToken ct = default) => _inner.RestoreCipherAsync(id, ct);
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => _inner.SaveFolderAsync(folderId, name, ct);
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => _inner.DeleteFolderAsync(folderId, ct);
        public Task SyncAsync(CancellationToken ct = default)
        {
            SyncCalls++;
            return SyncGate.Task;
        }
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => _inner.MoveCiphersAsync(ids, folderId, ct);
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => _inner.DeleteCiphersAsync(ids, permanent, ct);
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => _inner.RestoreCiphersAsync(ids, ct);
    }

    private static Task<string?> NoPrompt(CancellationToken _) => Task.FromResult<string?>(null);
    private static ReopenableFileSource Source(string fileName, byte[] bytes) =>
        new(
            fileName,
            bytes.LongLength,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

    // 方向一:附件下载在途 → 保存条目返回 Busy,SaveCipherAsync 从未被调用。
    [Fact]
    public async Task DownloadAttachmentInFlight_SaveDraftReturnsBusyWithoutCallingSave()
    {
        var vaultService = new GatedVaultUiService();
        var attachments = new GatedAttachmentUiService();
        var vm = new VaultViewModel(vaultService, attachments: attachments);

        var download = vm.DownloadAttachmentAsync(
            "1",
            "a1",
            NoPrompt,
            new MemoryStream(),
            CancellationToken.None);
        Assert.Equal(VaultOperationKind.AttachmentDownload, vm.CurrentOperation);
        Assert.Equal("正在下载附件…", vm.BusyMessage);
        Assert.False(vm.CanStartVaultOperation);

        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.Name = "Blocked";
        var save = await vm.SaveDraftAsync();

        Assert.Equal(UiOperationStatus.Busy, save.Status);
        Assert.Equal(0, vaultService.SaveCalls);
        Assert.Equal(1, attachments.DownloadCalls);

        attachments.Gate.SetResult(true);
        Assert.True((await download).IsSuccess);
        Assert.True(vm.CanStartVaultOperation);
    }

    // 方向二:附件上传在途 → 同步返回 Busy,SyncAsync 从未被调用。
    [Fact]
    public async Task AddAttachmentInFlight_SyncReturnsBusyWithoutCallingSync()
    {
        var vaultService = new GatedVaultUiService();
        var attachments = new GatedAttachmentUiService();
        var vm = new VaultViewModel(vaultService, attachments: attachments);

        var upload = vm.AddAttachmentAsync("1", Source("f.pdf", [1]), CancellationToken.None);
        Assert.Equal(VaultOperationKind.AttachmentUpload, vm.CurrentOperation);
        Assert.Equal("正在上传附件…", vm.BusyMessage);
        Assert.False(vm.CanStartVaultOperation);

        var sync = await vm.SyncAsync();

        Assert.Equal(UiOperationStatus.Busy, sync.Status);
        Assert.Equal(0, vaultService.SyncCalls);
        Assert.Equal(1, attachments.AddCalls);

        attachments.Gate.SetResult(true);
        Assert.True((await upload).IsSuccess);
        Assert.True(vm.CanStartVaultOperation);
    }

    [Fact]
    public async Task AddAttachmentAsync_WhileInFlight_SetsCurrentOperation_ThenClears()
    {
        var att = new GatedAttachmentUiService();
        var vm = new VaultViewModel(new MockVaultUiService(), attachments: att);

        var task = vm.AddAttachmentAsync("c1", Source("f.pdf", [1]), CancellationToken.None);

        Assert.Equal(VaultOperationKind.AttachmentUpload, vm.CurrentOperation);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanStartVaultOperation);

        att.Gate.SetResult(true);
        Assert.True((await task).IsSuccess);

        Assert.Null(vm.CurrentOperation);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanStartVaultOperation);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhileInFlight_SetsCurrentOperation_ThenClears()
    {
        var att = new GatedAttachmentUiService();
        var vm = new VaultViewModel(new MockVaultUiService(), attachments: att);

        var task = vm.DownloadAttachmentAsync(
            "1",
            "a1",
            NoPrompt,
            new MemoryStream(),
            CancellationToken.None);

        Assert.Equal(VaultOperationKind.AttachmentDownload, vm.CurrentOperation);
        Assert.True(vm.IsBusy);

        att.Gate.SetResult(true);
        await task;

        Assert.Null(vm.CurrentOperation);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DeleteAttachmentAsync_WhileInFlight_SetsCurrentOperation_ThenClears()
    {
        var att = new GatedAttachmentUiService();
        var vm = new VaultViewModel(new MockVaultUiService(), attachments: att);

        var task = vm.DeleteAttachmentAsync("c1", "a1", CancellationToken.None);

        Assert.Equal(VaultOperationKind.AttachmentDelete, vm.CurrentOperation);
        Assert.True(vm.IsBusy);

        att.Gate.SetResult(true);
        await task;

        Assert.Null(vm.CurrentOperation);
        Assert.False(vm.IsBusy);
    }

    // 已知的网络类异常经 RunOperationAsync 统一映射为安全文案(不泄露原始异常细节);
    // 未知异常类型交由 Task 12 统一处理,本文件只覆盖 HttpRequestException 这条已知路径。
    [Fact]
    public async Task AddAttachmentAsync_WhenServiceThrowsHttpRequestException_SetsSafeOperationError()
    {
        var att = new GatedAttachmentUiService { ThrowOnRelease = true };
        var vm = new VaultViewModel(new MockVaultUiService(), attachments: att);

        var task = vm.AddAttachmentAsync("c1", Source("f.pdf", [1]), CancellationToken.None);
        Assert.Equal(VaultOperationKind.AttachmentUpload, vm.CurrentOperation);

        att.Gate.SetResult(true);
        var result = await task;

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Null(vm.CurrentOperation);
        Assert.False(vm.IsBusy);
        Assert.Equal("网络请求失败，请检查连接后重试。", vm.OperationError);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhenServiceThrowsHttpRequestException_SetsSafeOperationError()
    {
        var att = new GatedAttachmentUiService { ThrowOnRelease = true };
        var vm = new VaultViewModel(new MockVaultUiService(), attachments: att);

        var task = vm.DownloadAttachmentAsync(
            "1",
            "a1",
            NoPrompt,
            new MemoryStream(),
            CancellationToken.None);
        Assert.Equal(VaultOperationKind.AttachmentDownload, vm.CurrentOperation);

        att.Gate.SetResult(true);
        var result = await task;

        Assert.Equal(0L, result.Value);
        Assert.False(vm.IsBusy);
        Assert.Equal("网络请求失败，请检查连接后重试。", vm.OperationError);
    }

    [Fact]
    public async Task DeleteAttachmentAsync_WhenServiceThrowsHttpRequestException_SetsSafeOperationError()
    {
        var att = new GatedAttachmentUiService { ThrowOnRelease = true };
        var vm = new VaultViewModel(new MockVaultUiService(), attachments: att);

        var task = vm.DeleteAttachmentAsync("c1", "a1", CancellationToken.None);
        Assert.Equal(VaultOperationKind.AttachmentDelete, vm.CurrentOperation);

        att.Gate.SetResult(true);
        var result = await task;

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.False(vm.IsBusy);
        Assert.Equal("网络请求失败，请检查连接后重试。", vm.OperationError);
    }
}
