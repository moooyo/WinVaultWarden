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
        var svc = new SendUiService(read, new FakeSendWriteService(), new FakeSendAccessService());

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
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService());
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
        Assert.Equal("pw", Assert.IsType<SendPasswordChange.Set>(saved.PasswordChange).Password);
        Assert.Null(saved.Id);
        Assert.True(saved.DeletionDate > DateTimeOffset.UtcNow.AddDays(6));
        Assert.True(saved.DeletionDate <= DateTimeOffset.UtcNow.AddDays(8));
    }

    [Fact]
    public async Task CreateSendAsync_FileDraft_CreatesWithReopenableSource()
    {
        var write = new FakeSendWriteService();
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService());
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.Name = "合同.zip";
        draft.FileName = "合同.zip";
        draft.DeletionDateLabel = "30 天";
        var bytes = new byte[] { 1, 2, 3, 4 };

        await svc.CreateSendAsync(draft, bytes, CancellationToken.None);

        Assert.Equal(CoreSendType.File, write.LastCreateDraft!.Type);
        Assert.Equal("合同.zip", write.LastCreateDraft.FileName);
        var source = write.LastCreateSource!;
        Assert.Equal("合同.zip", source.FileName);
        Assert.Equal(bytes.LongLength, source.Length);
        // 主契约要求可重开源：连续两次打开都得到相同内容。
        Assert.Equal(bytes, await ReadAll(source));
        Assert.Equal(bytes, await ReadAll(source));
    }

    private static async Task<byte[]> ReadAll(Core.Models.SendFileSource source)
    {
        await using var stream = await source.OpenReadAsync(CancellationToken.None);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task DeleteSendAsync_DelegatesToWriteService()
    {
        var write = new FakeSendWriteService();
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService());

        await svc.DeleteSendAsync("s9", CancellationToken.None);

        Assert.Equal("s9", write.LastDeletedId);
    }

    [Fact]
    public async Task GetSendsAsync_LinkUsesSendShareUrl_NotSeedlessFallback()
    {
        // C1 回归：UI 层 Link 必须使用 Send.ShareUrl（含 seed），而非无 seed 的拼接值。
        const string expectedShareUrl = "https://vault.example/#/send/acc-s1/AQIDBA";
        var coreRead = new FakeSendService(
            new Send
            {
                Id = "s1",
                AccessId = "acc-s1",
                Type = CoreSendType.Text,
                Name = "Test",
                DeletionDate = DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
                ShareUrl = expectedShareUrl,
            });
        var svc = new SendUiService(coreRead, new FakeSendWriteService(), new FakeSendAccessService());

        var items = await svc.GetSendsAsync(CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(expectedShareUrl, item.Link);
        // 明确断言 Link 不是无 seed 的拼接值（防回归）。
        Assert.NotEqual("https://vault.example/#/send/acc-s1", item.Link);
    }

    [Fact]
    public void CopyShareLink_ReturnsItemLink()
    {
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), new FakeSendAccessService());
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
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access);

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
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access);

        var r = await svc.OpenReceivedLinkAsync("https://vault.example/#/send/acc/seed", "bad", CancellationToken.None);

        Assert.False(r.Ok);
        Assert.True(r.WrongPassword);
    }

    [Fact]
    public async Task GetEditDraftAsync_FetchesFullSend_AndHydratesDraft()
    {
        var full = new Send
        {
            Id = "s7",
            AccessId = "acc7",
            Type = CoreSendType.Text,
            Name = "编辑我",
            Notes = "备注",
            Text = new SendText("正文", true),
            MaxAccessCount = 4,
            HideEmail = true,
            Disabled = true,
            DeletionDate = DateTimeOffset.UtcNow.AddDays(6),
            Key = "2.wrapped==",
        };
        var svc = new SendUiService(new FakeSendService(full), new FakeSendWriteService(), new FakeSendAccessService());

        var draft = await svc.GetEditDraftAsync("s7", CancellationToken.None);

        Assert.NotNull(draft);
        Assert.Equal("编辑我", draft!.Name);
        Assert.Equal("正文", draft.Text);
        Assert.Equal("备注", draft.Notes);
        Assert.True(draft.HideTextByDefault);
        Assert.Equal(4, draft.MaxAccessCount);
        Assert.Equal("2.wrapped==", draft.WrappedKey);
    }

    [Fact]
    public async Task GetEditDraftAsync_UnknownId_ReturnsNull()
    {
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), new FakeSendAccessService());

        Assert.Null(await svc.GetEditDraftAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSendAsync_FileDraft_MapsWrappedKeyAndIdIntoModel()
    {
        // F2a:更新文件 Send 时,draft.WrappedKey 必须传入 SendDraftModel.Key,
        // 供写入层复用原 seed(否则文件在服务端将无法解密)。
        var write = new FakeSendWriteService();
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService());
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.Name = "报告";
        draft.FileName = "报告.pdf";
        draft.WrappedKey = "2.origwrappedkey==";
        draft.DeletionDateLabel = "7 天";

        await svc.UpdateSendAsync("s-5", draft, fileBytes: null, CancellationToken.None);

        var saved = write.LastMetadataDraft!;
        Assert.Equal("s-5", saved.Id);
        Assert.Equal("2.origwrappedkey==", saved.Key);
        Assert.Equal(CoreSendType.File, saved.Type);
        // 未重选文件（fileBytes==null）绝不触发创建/替换，只更新元数据。
        Assert.Null(write.LastCreateSource);
        Assert.Null(write.LastReplaceSource);
    }

    [Fact]
    public async Task UpdateSendAsync_FileDraft_WithNewBytes_ReplacesExistingViaStream()
    {
        // 编辑既有文件 Send 且重选了文件字节 → 走 ReplaceFileSendAsync（建+传新件再删旧件），
        // App 边界把字节包装成可重开源，绝不把 null 当零字节，也不再走 byte[] 上传。
        var write = new FakeSendWriteService();
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService());
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.Name = "报告";
        draft.FileName = "报告-v2.pdf";
        draft.WrappedKey = "2.origwrappedkey==";
        draft.DeletionDateLabel = "7 天";
        var bytes = new byte[] { 9, 8, 7, 6 };

        await svc.UpdateSendAsync("s-5", draft, bytes, CancellationToken.None);

        Assert.Equal("s-5", write.LastReplaceId);
        Assert.Equal(CoreSendType.File, write.LastReplaceDraft!.Type);
        Assert.Equal("2.origwrappedkey==", write.LastReplaceDraft.Key);
        Assert.Equal("报告-v2.pdf", write.LastReplaceSource!.FileName);
        Assert.Equal(bytes.LongLength, write.LastReplaceSource.Length);
        Assert.Null(write.LastMetadataDraft);
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
        public SendDraftModel? LastCreateDraft { get; private set; }
        public SendFileSource? LastCreateSource { get; private set; }
        public SendDraftModel? LastMetadataDraft { get; private set; }
        public string? LastReplaceId { get; private set; }
        public SendDraftModel? LastReplaceDraft { get; private set; }
        public SendFileSource? LastReplaceSource { get; private set; }
        public string? LastDeletedId { get; private set; }

        public Task SaveTextSendAsync(SendDraftModel draft, CancellationToken ct = default)
        { LastTextDraft = draft; return Task.CompletedTask; }
        public Task<SendFileWriteResult> CreateFileSendAsync(SendDraftModel draft, SendFileSource file, CancellationToken ct = default)
        { LastCreateDraft = draft; LastCreateSource = file; return Task.FromResult(new SendFileWriteResult("new", "acc-new", false)); }
        public Task UpdateFileSendMetadataAsync(SendDraftModel draft, CancellationToken ct = default)
        { LastMetadataDraft = draft; return Task.CompletedTask; }
        public Task<SendFileWriteResult> ReplaceFileSendAsync(string existingSendId, SendDraftModel draft, SendFileSource file, CancellationToken ct = default)
        { LastReplaceId = existingSendId; LastReplaceDraft = draft; LastReplaceSource = file; return Task.FromResult(new SendFileWriteResult("new", "acc-new", true)); }
        public Task DeleteSendAsync(string sendId, CancellationToken ct = default)
        { LastDeletedId = sendId; return Task.CompletedTask; }
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
