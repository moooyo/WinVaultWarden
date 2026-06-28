using App.Services;
using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Xunit;
using AppSendType = App.ViewModels.Models.SendType;
using CoreSendType = Core.Enums.SendType;

namespace App.Tests;

public class RealSendUiServiceTests
{
    private static Send CoreSend(string id, CoreSendType type, string name, bool hasPw = false) => new()
    {
        Id = id,
        AccessId = "acc-" + id,
        Type = type,
        Name = name,
        DeletionDate = DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        HasPassword = hasPw,
        Text = type == CoreSendType.Text ? new SendText("body", false) : null,
        File = type == CoreSendType.File ? new SendFile(name, 1234, "1.2 KB", "f1") : null,
    };

    [Fact]
    public async Task GetSendsAsync_MapsCoreSendsToListItems_AndMapsTypeEnum()
    {
        var read = new FakeSendService(
            CoreSend("s1", CoreSendType.Text, "口令"),
            CoreSend("s2", CoreSendType.File, "报告.pdf"));
        var svc = new SendUiService(read, new FakeSendWriteService(), new FakeSendAccessService(),
            serverUrl: "https://vault.example");

        var items = await svc.GetSendsAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(AppSendType.Text, items[0].Type);
        Assert.Equal(AppSendType.File, items[1].Type);
        Assert.Equal("口令", items[0].Name);
    }

    [Fact]
    public async Task CreateSendAsync_TextDraft_CallsSaveTextWithMappedDraftAndAbsoluteDeletionDate()
    {
        var write = new FakeSendWriteService();
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), "https://vault.example");
        var draft = SendEditorDraft.CreateDefault(AppSendType.Text);
        draft.Name = "一次性口令";
        draft.Text = "123456";
        draft.HideTextByDefault = true;
        draft.DeletionDateLabel = "7 天";
        draft.MaxAccessCount = 3;
        draft.Disabled = true;
        draft.HideEmail = true;
        draft.Password = "pw";

        await svc.CreateSendAsync(draft, fileBytes: null, CancellationToken.None);

        var saved = write.LastTextDraft!;
        Assert.Equal(CoreSendType.Text, saved.Type);
        Assert.Equal("一次性口令", saved.Name);
        Assert.Equal("123456", saved.TextContent);
        Assert.True(saved.TextHidden);
        Assert.Equal(3, saved.MaxAccessCount);
        Assert.True(saved.Disabled);
        Assert.True(saved.HideEmail);
        Assert.Equal("pw", saved.Password);
        Assert.Null(saved.Id);
        Assert.True(saved.DeletionDate > DateTimeOffset.UtcNow.AddDays(6));
        Assert.True(saved.DeletionDate <= DateTimeOffset.UtcNow.AddDays(8));
    }

    [Fact]
    public async Task CreateSendAsync_FileDraft_CallsSaveFileWithBytes()
    {
        var write = new FakeSendWriteService();
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), "https://vault.example");
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.Name = "合同.zip";
        draft.FileName = "合同.zip";
        draft.DeletionDateLabel = "30 天";
        var bytes = new byte[] { 1, 2, 3, 4 };

        await svc.CreateSendAsync(draft, bytes, CancellationToken.None);

        Assert.Equal(CoreSendType.File, write.LastFileDraft!.Type);
        Assert.Equal("合同.zip", write.LastFileDraft.FileName);
        Assert.Equal(bytes, write.LastFileBytes);
    }

    [Fact]
    public async Task DeleteSendAsync_DelegatesToWriteService()
    {
        var write = new FakeSendWriteService();
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), "https://vault.example");

        await svc.DeleteSendAsync("s9", CancellationToken.None);

        Assert.Equal("s9", write.LastDeletedId);
    }

    [Fact]
    public void CopyShareLink_ReturnsItemLink()
    {
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), new FakeSendAccessService(), "https://vault.example");
        var item = new SendListItem("s1", "n", AppSendType.Text, "del", "https://vault.example/#/send/acc/seed");

        Assert.Equal("https://vault.example/#/send/acc/seed", svc.CopyShareLink(item));
    }

    [Fact]
    public async Task OpenReceivedLinkAsync_Text_ReturnsOkWithContent()
    {
        var access = new FakeSendAccessService
        {
            Result = new SendAccessResult
            {
                Type = CoreSendType.Text, Name = "分享", TextContent = "hello world",
                AccessId = "acc", Seed = new byte[16],
            },
        };
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access, "https://vault.example");

        var r = await svc.OpenReceivedLinkAsync("https://vault.example/#/send/acc/seed", null, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.False(r.WrongPassword);
        Assert.Equal(AppSendType.Text, r.Type);
        Assert.Equal("hello world", r.TextContent);
    }

    [Fact]
    public async Task OpenReceivedLinkAsync_WrongPassword_ReturnsWrongPasswordResult()
    {
        var access = new FakeSendAccessService { ThrowUnauthorized = true };
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access, "https://vault.example");

        var r = await svc.OpenReceivedLinkAsync("https://vault.example/#/send/acc/seed", "bad", CancellationToken.None);

        Assert.False(r.Ok);
        Assert.True(r.WrongPassword);
    }

    private sealed class FakeSendService : ISendService
    {
        private readonly Send[] _sends;
        public FakeSendService(params Send[] sends) => _sends = sends;
        public Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Send>>(_sends);
    }

    private sealed class FakeSendWriteService : ISendWriteService
    {
        public SendDraftModel? LastTextDraft { get; private set; }
        public SendDraftModel? LastFileDraft { get; private set; }
        public byte[]? LastFileBytes { get; private set; }
        public string? LastDeletedId { get; private set; }

        public Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default)
        { LastTextDraft = draft; return Task.CompletedTask; }
        public Task SaveFileSendAsync(SendDraftModel draft, byte[] fileBytes, CancellationToken ct = default)
        { LastFileDraft = draft; LastFileBytes = fileBytes; return Task.CompletedTask; }
        public Task DeleteSendAsync(string sendId, CancellationToken ct = default)
        { LastDeletedId = sendId; return Task.CompletedTask; }
        public Task<Send> RemovePasswordAsync(string sendId, CancellationToken ct = default)
        => Task.FromResult(new Send { Id = sendId, AccessId = "acc", DeletionDate = DateTimeOffset.UtcNow });
    }

    private sealed class FakeSendAccessService : ISendAccessService
    {
        public SendAccessResult? Result { get; set; }
        public bool ThrowUnauthorized { get; set; }
        public byte[] Downloaded { get; set; } = Array.Empty<byte>();

        public Task<SendAccessResult> AccessAsync(string shareUrl, string? password, CancellationToken ct = default)
        {
            if (ThrowUnauthorized)
                throw new UnauthorizedAccessException("send password invalid");
            return Task.FromResult(Result ?? new SendAccessResult
            {
                Type = Core.Enums.SendType.Text, Name = "n", AccessId = "acc", Seed = new byte[16],
            });
        }

        public Task<byte[]> DownloadFileAsync(SendAccessResult accessed, CancellationToken ct = default)
            => Task.FromResult(Downloaded);
    }
}
