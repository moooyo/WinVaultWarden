using App.Services;
using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Vault;
using Xunit;
using AppSendType = App.ViewModels.Models.SendType;
using CoreSendType = Core.Enums.SendType;

namespace App.Tests;

// 演示/离线会话:SendUiService 必须完全走 VaultSession 内存存储,绝不调用底层网络 Send 服务
// (否则会触发「An invalid request URI was provided…」,即用户报告的 Send 页报错)。
// 三个底层 fake 一旦被调用即抛异常,从而把「不触网」这一契约固化进测试。
public class SendUiServiceDemoTests
{
    private static Send CoreSend(string id, CoreSendType type, string name) => new()
    {
        Id = id,
        AccessId = "acc-" + id,
        Type = type,
        Name = name,
        DeletionDate = DateTimeOffset.Parse("2026-07-15T00:00:00Z"),
        ShareUrl = $"https://demo.local/#/send/{id}/demoAccessKey",
        Text = type == CoreSendType.Text ? new SendText("body", false) : null,
        File = type == CoreSendType.File ? new SendFile(name, 1234, "1.2 KB", "f1") : null,
    };

    private static VaultSession DemoSessionWith(params Send[] sends)
    {
        var session = new VaultSession();
        session.SetSends(sends);
        session.MarkDemo();
        return session;
    }

    private static SendUiService DemoService(VaultSession session) =>
        new(new ThrowingSendService(), new ThrowingSendWriteService(), new ThrowingSendAccessService(), session);

    [Fact]
    public async Task GetSendsAsync_InDemoMode_ReturnsSessionSends_WithoutHittingNetwork()
    {
        var session = DemoSessionWith(
            CoreSend("s1", CoreSendType.Text, "文本分享"),
            CoreSend("s2", CoreSendType.File, "报告.pdf"));
        var svc = DemoService(session);

        var items = await svc.GetSendsAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(AppSendType.Text, items[0].Type);
        Assert.Equal(AppSendType.File, items[1].Type);
        Assert.Equal("文本分享", items[0].Name);
    }

    [Fact]
    public async Task CreateSendAsync_InDemoMode_AddsToSessionStore()
    {
        var session = DemoSessionWith();
        var svc = DemoService(session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.Text);
        draft.Name = "一次性口令";
        draft.Text = "123456";

        var item = await svc.CreateSendAsync(draft, fileBytes: null, CancellationToken.None);

        Assert.Equal("一次性口令", item.Name);
        var stored = Assert.Single(session.Sends);
        Assert.Equal(item.Id, stored.Id);
        Assert.Equal("123456", stored.Text!.Content);
    }

    [Fact]
    public async Task CreateSendAsync_InDemoMode_FileDraft_StoresFileNameAndSize()
    {
        var session = DemoSessionWith();
        var svc = DemoService(session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.Name = "合同";
        draft.FileName = "合同.zip";

        await svc.CreateSendAsync(draft, new byte[] { 1, 2, 3, 4 }, CancellationToken.None);

        var stored = Assert.Single(session.Sends);
        Assert.Equal(CoreSendType.File, stored.Type);
        Assert.Equal("合同.zip", stored.File!.FileName);
        Assert.Equal(4, stored.File.Size);
    }

    [Fact]
    public async Task UpdateSendAsync_InDemoMode_ReplacesExistingByIdWithoutGrowingList()
    {
        var session = DemoSessionWith(CoreSend("s1", CoreSendType.Text, "旧名"));
        var svc = DemoService(session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.Text);
        draft.Name = "新名";
        draft.Text = "updated";

        var item = await svc.UpdateSendAsync("s1", draft, fileBytes: null, CancellationToken.None);

        Assert.Equal("s1", item.Id);
        var stored = Assert.Single(session.Sends);
        Assert.Equal("s1", stored.Id);
        Assert.Equal("新名", stored.Name);
        Assert.Equal("updated", stored.Text!.Content);
    }

    [Fact]
    public async Task DeleteSendAsync_InDemoMode_RemovesFromSessionStore()
    {
        var session = DemoSessionWith(
            CoreSend("s1", CoreSendType.Text, "保留"),
            CoreSend("s2", CoreSendType.Text, "删除"));
        var svc = DemoService(session);

        await svc.DeleteSendAsync("s2", CancellationToken.None);

        var remaining = Assert.Single(session.Sends);
        Assert.Equal("s1", remaining.Id);
    }

    [Fact]
    public async Task GetEditDraftAsync_InDemoMode_HydratesFromSessionStore()
    {
        var session = DemoSessionWith(CoreSend("s7", CoreSendType.Text, "编辑我"));
        var svc = DemoService(session);

        var draft = await svc.GetEditDraftAsync("s7", CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("编辑我", draft!.Name);
        Assert.Equal("body", draft.Text);
    }

    [Fact]
    public async Task GetEditDraftAsync_InDemoMode_UnknownId_ReturnsNull()
    {
        var svc = DemoService(DemoSessionWith(CoreSend("s7", CoreSendType.Text, "x")));

        Assert.Null(await svc.GetEditDraftAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task OpenReceivedLinkAsync_InDemoMode_ReturnsFriendlyFailure_WithoutHittingNetwork()
    {
        var svc = DemoService(DemoSessionWith());

        var r = await svc.OpenReceivedLinkAsync("https://demo.local/#/send/x/y", null, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.False(r.WrongPassword);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public async Task GetSendsAsync_NonDemoSession_DelegatesToNetworkRead()
    {
        // 未标记演示的会话仍走在线路径:委派给 ISendService。
        var session = new VaultSession(); // IsDemo == false
        var read = new StubSendService(CoreSend("s1", CoreSendType.Text, "在线"));
        var svc = new SendUiService(read, new ThrowingSendWriteService(), new ThrowingSendAccessService(), session);

        var items = await svc.GetSendsAsync(CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("在线", item.Name);
    }

    private sealed class StubSendService : ISendService
    {
        private readonly Send[] _sends;
        public StubSendService(params Send[] sends) => _sends = sends;
        public Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Send>>(_sends);
    }

    private sealed class ThrowingSendService : ISendService
    {
        public Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("network read must not run in demo mode");
    }

    private sealed class ThrowingSendWriteService : ISendWriteService
    {
        public Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task SaveFileSendAsync(SendDraftModel draft, byte[] fileBytes, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task DeleteSendAsync(string sendId, CancellationToken ct = default) =>
            throw new InvalidOperationException("network delete must not run in demo mode");
        public Task<Send> RemovePasswordAsync(string sendId, CancellationToken ct = default) =>
            throw new InvalidOperationException("network op must not run in demo mode");
    }

    private sealed class ThrowingSendAccessService : ISendAccessService
    {
        public Task<SendAccessResult> AccessAsync(string shareUrl, string? password, CancellationToken ct = default) =>
            throw new InvalidOperationException("network access must not run in demo mode");
        public Task<byte[]> DownloadFileAsync(SendAccessResult accessed, CancellationToken ct = default) =>
            throw new InvalidOperationException("network download must not run in demo mode");
    }
}
