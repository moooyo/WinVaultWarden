using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class VaultViewModelAttachmentTests
{
    // 让 GetDetail 返回带附件的 LoginDetail；Attachments 由测试预置，模拟 re-sync 后的快照投影。
    private sealed class AttachmentVaultUiService : IVaultUiService
    {
        private readonly MockVaultUiService _inner = new();
        public IReadOnlyList<AttachmentItem> DetailAttachments { get; set; } = Array.Empty<AttachmentItem>();
        public string? LastDetailId { get; private set; }

        public IReadOnlyList<CipherListItem> GetItems() => _inner.GetItems();
        public IReadOnlyList<FilterNode> GetFilters() => _inner.GetFilters();
        public CipherEditorDraft GetDraft(string id) => _inner.GetDraft(id);

        public CipherDetail GetDetail(string id)
        {
            LastDetailId = id;
            var baseDetail = _inner.GetDetail(id);
            return new LoginDetail
            {
                Id = baseDetail.Id,
                Name = baseDetail.Name,
                Attachments = DetailAttachments,
            };
        }

        public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default) => _inner.SaveCipherAsync(draft, editingId, ct);
        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => _inner.DeleteCipherAsync(id, permanent, ct);
        public Task RestoreCipherAsync(string id, CancellationToken ct = default) => _inner.RestoreCipherAsync(id, ct);
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => _inner.SaveFolderAsync(folderId, name, ct);
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => _inner.DeleteFolderAsync(folderId, ct);
        public Task SyncAsync(CancellationToken ct = default) => _inner.SyncAsync(ct);
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => _inner.MoveCiphersAsync(ids, folderId, ct);
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => _inner.DeleteCiphersAsync(ids, permanent, ct);
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => _inner.RestoreCiphersAsync(ids, ct);
    }

    private sealed class FakeAttachmentUiService : IAttachmentUiService
    {
        public string? LastCipherId { get; private set; }
        public string? LastFileName { get; private set; }
        public byte[]? LastFileBytes { get; private set; }
        public string? LastAttachmentId { get; private set; }
        public bool ThrowOnAdd { get; set; }
        public bool ThrowOnDownload { get; set; }
        public bool ThrowOnDelete { get; set; }

        public IReadOnlyList<AttachmentItem> AddResult { get; set; } = Array.Empty<AttachmentItem>();
        public byte[] DownloadResult { get; set; } = Array.Empty<byte>();
        public IReadOnlyList<AttachmentItem> DeleteResult { get; set; } = Array.Empty<AttachmentItem>();

        public Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(string cipherId, byte[] fileBytes, string fileName, CancellationToken ct = default)
        {
            if (ThrowOnAdd)
                throw new InvalidOperationException("upload failed");
            LastCipherId = cipherId;
            LastFileBytes = fileBytes;
            LastFileName = fileName;
            return Task.FromResult(AddResult);
        }

        public Task<byte[]> DownloadAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        {
            if (ThrowOnDownload)
                throw new InvalidOperationException("download failed");
            LastCipherId = cipherId;
            LastAttachmentId = attachmentId;
            return Task.FromResult(DownloadResult);
        }

        public Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        {
            if (ThrowOnDelete)
                throw new InvalidOperationException("delete failed");
            LastCipherId = cipherId;
            LastAttachmentId = attachmentId;
            return Task.FromResult(DeleteResult);
        }
    }

    private static VaultViewModel Vm(IVaultUiService vault, IAttachmentUiService att) =>
        new(vault, clipboard: null, attachments: att);

    [Fact]
    public async Task AddAttachmentAsync_DelegatesToServiceWithBytesAndFileName()
    {
        var att = new FakeAttachmentUiService { AddResult = [new AttachmentItem("a1", "f.pdf", "1 KB")] };
        var vm = Vm(new AttachmentVaultUiService(), att);
        var bytes = new byte[] { 1, 2, 3 };

        await vm.AddAttachmentAsync("c1", bytes, "f.pdf", CancellationToken.None);

        Assert.Equal("c1", att.LastCipherId);
        Assert.Same(bytes, att.LastFileBytes);
        Assert.Equal("f.pdf", att.LastFileName);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.OperationError);
    }

    [Fact]
    public async Task AddAttachmentAsync_WhenDetailIsCurrentCipher_RefreshesDetailAttachments()
    {
        var vault = new AttachmentVaultUiService
        {
            DetailAttachments = [new AttachmentItem("a1", "f.pdf", "1 KB")],
        };
        var att = new FakeAttachmentUiService { AddResult = [new AttachmentItem("a1", "f.pdf", "1 KB")] };
        var vm = Vm(vault, att);
        vm.SelectedItem = vm.Items.First(i => i.Id == "1"); // 触发 GetDetail，Detail.Id == "1"

        await vm.AddAttachmentAsync("1", new byte[] { 9 }, "f.pdf", CancellationToken.None);

        Assert.NotNull(vm.Detail);
        var item = Assert.Single(vm.Detail!.Attachments);
        Assert.Equal("a1", item.Id);
        Assert.Equal("f.pdf", item.FileName);
    }

    [Fact]
    public async Task AddAttachmentAsync_WhenServiceThrows_SetsOperationErrorAndClearsBusy()
    {
        var att = new FakeAttachmentUiService { ThrowOnAdd = true };
        var vm = Vm(new AttachmentVaultUiService(), att);

        await vm.AddAttachmentAsync("c1", new byte[] { 1 }, "f.pdf", CancellationToken.None);

        Assert.Equal("upload failed", vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_ReturnsBytesFromService()
    {
        var att = new FakeAttachmentUiService { DownloadResult = new byte[] { 7, 7, 7 } };
        var vm = Vm(new AttachmentVaultUiService(), att);

        var bytes = await vm.DownloadAttachmentAsync("c1", "a1", CancellationToken.None);

        Assert.Equal(new byte[] { 7, 7, 7 }, bytes);
        Assert.Equal("c1", att.LastCipherId);
        Assert.Equal("a1", att.LastAttachmentId);
        Assert.Equal(string.Empty, vm.OperationError);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhenServiceThrows_ReturnsNullAndSetsError()
    {
        var att = new FakeAttachmentUiService { ThrowOnDownload = true };
        var vm = Vm(new AttachmentVaultUiService(), att);

        var bytes = await vm.DownloadAttachmentAsync("c1", "a1", CancellationToken.None);

        Assert.Null(bytes);
        Assert.Equal("download failed", vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DeleteAttachmentAsync_DelegatesAndRefreshesDetailAttachments()
    {
        var vault = new AttachmentVaultUiService
        {
            DetailAttachments = [new AttachmentItem("a2", "kept.png", "2 KB")],
        };
        var att = new FakeAttachmentUiService { DeleteResult = [new AttachmentItem("a2", "kept.png", "2 KB")] };
        var vm = Vm(vault, att);
        vm.SelectedItem = vm.Items.First(i => i.Id == "1");

        await vm.DeleteAttachmentAsync("1", "a1", CancellationToken.None);

        Assert.Equal("1", att.LastCipherId);
        Assert.Equal("a1", att.LastAttachmentId);
        Assert.NotNull(vm.Detail);
        var item = Assert.Single(vm.Detail!.Attachments);
        Assert.Equal("a2", item.Id);
    }

    [Fact]
    public async Task DeleteAttachmentAsync_WhenServiceThrows_SetsOperationErrorAndClearsBusy()
    {
        var att = new FakeAttachmentUiService { ThrowOnDelete = true };
        var vm = Vm(new AttachmentVaultUiService(), att);

        await vm.DeleteAttachmentAsync("c1", "a1", CancellationToken.None);

        Assert.Equal("delete failed", vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AttachmentMethods_NoAttachmentService_NoOpWithoutThrowing()
    {
        // 未注入附件服务(可选依赖为 null)时,方法应安全短路,不抛异常。
        var vm = new VaultViewModel(new MockVaultUiService());

        await vm.AddAttachmentAsync("c1", new byte[] { 1 }, "f.pdf", CancellationToken.None);
        var bytes = await vm.DownloadAttachmentAsync("c1", "a1", CancellationToken.None);
        await vm.DeleteAttachmentAsync("c1", "a1", CancellationToken.None);

        Assert.Null(bytes);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.OperationError);
    }
}
