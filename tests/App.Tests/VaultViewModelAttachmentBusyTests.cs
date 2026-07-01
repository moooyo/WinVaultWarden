using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class VaultViewModelAttachmentBusyTests
{
    // 门控 fake:每个操作在被显式释放(SetResult)前保持挂起,以便断言"进行中"的忙碌文案。
    private sealed class GatedAttachmentUiService : IAttachmentUiService
    {
        public TaskCompletionSource<bool> Gate { get; } = new();
        public bool ThrowOnRelease { get; set; }

        public async Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(string cipherId, byte[] fileBytes, string fileName, CancellationToken ct = default)
        {
            await Gate.Task;
            if (ThrowOnRelease)
                throw new InvalidOperationException("upload failed");
            return Array.Empty<AttachmentItem>();
        }

        public async Task<byte[]> DownloadAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        {
            await Gate.Task;
            if (ThrowOnRelease)
                throw new InvalidOperationException("download failed");
            return Array.Empty<byte>();
        }

        public async Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        {
            await Gate.Task;
            if (ThrowOnRelease)
                throw new InvalidOperationException("delete failed");
            return Array.Empty<AttachmentItem>();
        }
    }

    private static VaultViewModel Vm(IAttachmentUiService att) =>
        new(new MockVaultUiService(), clipboard: null, attachments: att);

    [Fact]
    public async Task AddAttachmentAsync_WhileInFlight_ShowsUploadingText_ThenClears()
    {
        var att = new GatedAttachmentUiService();
        var vm = Vm(att);

        var task = vm.AddAttachmentAsync("c1", new byte[] { 1 }, "f.pdf", CancellationToken.None);

        Assert.Equal("上传中…", vm.AttachmentBusyText);
        Assert.True(vm.IsAttachmentBusy);
        Assert.False(vm.IsNotAttachmentBusy);

        att.Gate.SetResult(true);
        await task;

        Assert.Equal(string.Empty, vm.AttachmentBusyText);
        Assert.False(vm.IsAttachmentBusy);
        Assert.True(vm.IsNotAttachmentBusy);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhileInFlight_ShowsDownloadingText_ThenClears()
    {
        var att = new GatedAttachmentUiService();
        var vm = Vm(att);

        var task = vm.DownloadAttachmentAsync("c1", "a1", CancellationToken.None);

        Assert.Equal("下载中…", vm.AttachmentBusyText);
        Assert.True(vm.IsAttachmentBusy);

        att.Gate.SetResult(true);
        await task;

        Assert.Equal(string.Empty, vm.AttachmentBusyText);
        Assert.False(vm.IsAttachmentBusy);
    }

    [Fact]
    public async Task DeleteAttachmentAsync_WhileInFlight_ShowsDeletingText_ThenClears()
    {
        var att = new GatedAttachmentUiService();
        var vm = Vm(att);

        var task = vm.DeleteAttachmentAsync("c1", "a1", CancellationToken.None);

        Assert.Equal("删除中…", vm.AttachmentBusyText);
        Assert.True(vm.IsAttachmentBusy);

        att.Gate.SetResult(true);
        await task;

        Assert.Equal(string.Empty, vm.AttachmentBusyText);
        Assert.False(vm.IsAttachmentBusy);
    }

    [Fact]
    public async Task AddAttachmentAsync_WhenServiceThrows_ClearsBusyTextAndSetsOperationError()
    {
        var att = new GatedAttachmentUiService { ThrowOnRelease = true };
        var vm = Vm(att);

        var task = vm.AddAttachmentAsync("c1", new byte[] { 1 }, "f.pdf", CancellationToken.None);
        Assert.Equal("上传中…", vm.AttachmentBusyText);

        att.Gate.SetResult(true);
        await task;

        Assert.Equal(string.Empty, vm.AttachmentBusyText);
        Assert.False(vm.IsAttachmentBusy);
        Assert.Equal("upload failed", vm.OperationError);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhenServiceThrows_ClearsBusyTextAndSetsOperationError()
    {
        var att = new GatedAttachmentUiService { ThrowOnRelease = true };
        var vm = Vm(att);

        var task = vm.DownloadAttachmentAsync("c1", "a1", CancellationToken.None);
        Assert.Equal("下载中…", vm.AttachmentBusyText);

        att.Gate.SetResult(true);
        var bytes = await task;

        Assert.Null(bytes);
        Assert.Equal(string.Empty, vm.AttachmentBusyText);
        Assert.False(vm.IsAttachmentBusy);
        Assert.Equal("download failed", vm.OperationError);
    }

    [Fact]
    public async Task DeleteAttachmentAsync_WhenServiceThrows_ClearsBusyTextAndSetsOperationError()
    {
        var att = new GatedAttachmentUiService { ThrowOnRelease = true };
        var vm = Vm(att);

        var task = vm.DeleteAttachmentAsync("c1", "a1", CancellationToken.None);
        Assert.Equal("删除中…", vm.AttachmentBusyText);

        att.Gate.SetResult(true);
        await task;

        Assert.Equal(string.Empty, vm.AttachmentBusyText);
        Assert.False(vm.IsAttachmentBusy);
        Assert.Equal("delete failed", vm.OperationError);
    }
}
