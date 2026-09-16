using System.IO;
using System.Net.Http;
using Api;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Models;
using Core.Session;
using Xunit;

namespace App.Tests;

public class SendViewModelTests
{
    [Fact]
    public async Task FilterWithNoMatches_ShowsEmptyUntilReturningToAll()
    {
        var service = new FakeSendUiService();
        service.Sends.RemoveAll(item => item.Type == SendType.Text);
        var vm = await LoadedVmAsync(service);

        vm.SelectFilterByTag("send:text");
        Assert.True(vm.ShowEmpty);
        Assert.False(vm.ShowContent);

        vm.SelectFilterByTag("send:all");
        Assert.True(vm.ShowContent);
    }

    [Fact]
    public async Task DisabledSend_CopyProvidesActualClipboardAndUnavailableFeedback()
    {
        var clipboard = new RecordingClipboard();
        var vm = await LoadedVmAsync(new FakeSendUiService(), clipboard);
        var item = vm.Items[0] with { Disabled = true, DeletionDateTime = DateTimeOffset.UtcNow.AddDays(1) };

        vm.CopyLinkCommand.Execute(item);

        Assert.Equal(item.Link, clipboard.Text);
        Assert.Contains("已复制", vm.CopyFeedback);
        Assert.Contains("不可访问", vm.CopyFeedback);
        Assert.True(item.CanModify);
    }

    [Fact]
    public async Task ClipboardFailure_DoesNotPublishSuccess()
    {
        var vm = await LoadedVmAsync(new FakeSendUiService(), new RejectingClipboard());

        vm.CopyLinkCommand.Execute(vm.Items[0]);

        Assert.Null(vm.CopyFeedback);
        Assert.Contains("无法复制", vm.Error);
    }

    [Fact]
    public async Task MissingClipboard_DoesNotPublishSuccess()
    {
        var vm = await LoadedVmAsync(new FakeSendUiService());

        vm.CopyLinkCommand.Execute(vm.Items[0]);

        Assert.Null(vm.CopyFeedback);
        Assert.True(vm.HasError);
    }

    private sealed class RejectingClipboard : IClipboardService
    {
        public void SetText(string text) => throw new InvalidOperationException("Clipboard is busy.");
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public int SecretCount { get; private set; }
        public int PlainCount { get; private set; }
        public void SetText(string text) { Text = text; PlainCount++; }
        public void SetSecretText(string text, int? autoClearSeconds = null) { Text = text; SecretCount++; }
    }

    private sealed class FakeSendUiService : ISendUiService
    {
        public List<SendListItem> Sends { get; } = new()
        {
            new("s1", "周报.pdf", SendType.File, "2026-07-01 截止", "https://vault.example/#/send/acc1/seed1"),
            new("s2", "临时口令", SendType.Text, "2026-06-25 截止", "https://vault.example/#/send/acc2/seed2"),
        };

        public SendEditorDraft? LastCreateDraft { get; private set; }
        public SendFileSource? LastCreateFile { get; private set; }
        public SendEditorDraft? LastUpdateDraft { get; private set; }
        public SendFileSource? LastUpdateFile { get; private set; }
        public string? LastUpdateId { get; private set; }
        public string? LastDeletedId { get; private set; }
        public string? LastOpenedUrl { get; private set; }
        public string? LastOpenedPassword { get; private set; }
        public SendReceivedResult? ScriptedReceived { get; set; }
        public Exception? CreateError { get; set; }
        public Exception? DeleteError { get; set; }
        public Exception? OpenError { get; set; }

        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SendListItem>>(Sends.ToList());

        public string? LastEditDraftId { get; private set; }
        public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default)
        {
            LastEditDraftId = id;
            var send = Sends.FirstOrDefault(s => s.Id == id);
            if (send is null) return Task.FromResult<SendEditorDraft?>(null);
            var draft = SendEditorDraft.CreateDefault(send.Type);
            draft.Name = send.Name;
            return Task.FromResult<SendEditorDraft?>(draft);
        }

        public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
        {
            LastCreateDraft = draft;
            LastCreateFile = file;
            if (CreateError is not null)
                return Task.FromException<SendListItem>(CreateError);
            var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
            return Task.FromResult(new SendListItem("new-1", name, draft.Type, "新建", "https://vault.example/#/send/new/seed"));
        }

        public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
        {
            LastUpdateId = id;
            LastUpdateDraft = draft;
            LastUpdateFile = file;
            var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
            return Task.FromResult(new SendListItem(id, name, draft.Type, "已更新", "https://vault.example/#/send/upd/seed"));
        }

        public Task DeleteSendAsync(
            string id,
            VaultSessionLease originatingSession,
            CancellationToken ct = default)
        {
            LastDeletedId = id;
            if (DeleteError is not null)
                return Task.FromException(DeleteError);
            Sends.RemoveAll(s => s.Id == id);
            return Task.CompletedTask;
        }

        public string? CopyShareLink(SendListItem item) => item.Link;

        public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default)
        {
            LastOpenedUrl = url;
            LastOpenedPassword = password;
            if (OpenError is not null)
                return Task.FromException<SendReceivedResult>(OpenError);
            return Task.FromResult(ScriptedReceived ?? new SendReceivedResult(true, false, SendType.Text, "分享", "明文内容", null, null, null));
        }
    }

    private static SendFileSource MemorySource(string name, byte[] bytes) => new(
        name,
        bytes.LongLength,
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

    private static async Task<SendViewModel> LoadedVmAsync(FakeSendUiService svc, IClipboardService? clip = null)
    {
        var vm = new SendViewModel(svc, clip);
        await vm.LoadAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task LoadAsync_PopulatesItemsAndShowsContent()
    {
        var vm = await LoadedVmAsync(new FakeSendUiService());

        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(2, vm.FilteredItems.Count);
        Assert.True(vm.HasItems);
        Assert.True(vm.ShowContent);
        Assert.Equal(DataPageState.Content, vm.LoadState);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadAsync_NoSends_ShowsEmptyState()
    {
        var svc = new FakeSendUiService();
        svc.Sends.Clear();

        var vm = await LoadedVmAsync(svc);

        Assert.Empty(vm.Items);
        Assert.True(vm.ShowEmpty);
        Assert.Equal(DataPageState.Empty, vm.LoadState);
    }

    [Fact]
    public async Task LoadAsync_ServiceThrows_ShowsErrorState()
    {
        var vm = new SendViewModel(new ThrowingSendUiService());

        var result = await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.True(vm.HasError);
        Assert.True(vm.ShowError);
        Assert.Equal(DataPageState.Error, vm.LoadState);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadAsync_LatestWins_OldResultDoesNotOverwriteNew()
    {
        var oldLoad = new TaskCompletionSource<IReadOnlyList<SendListItem>>();
        var newLoad = new TaskCompletionSource<IReadOnlyList<SendListItem>>();
        var svc = new DeferredSendUiService(oldLoad.Task, newLoad.Task);
        var vm = new SendViewModel(svc);

        var oldTask = vm.LoadAsync(CancellationToken.None); // 消费 oldLoad
        var newTask = vm.LoadAsync(CancellationToken.None); // 消费 newLoad,并使 old 失效

        var newItems = new List<SendListItem> { new("new", "新", SendType.Text, "d", null) };
        var oldItems = new List<SendListItem> { new("old", "旧", SendType.Text, "d", null) };
        newLoad.SetResult(newItems); // 先完成新请求
        var newResult = await newTask;
        oldLoad.SetResult(oldItems); // 后完成旧请求
        var oldResult = await oldTask;

        Assert.True(newResult.IsSuccess);
        Assert.Equal(UiOperationStatus.Cancelled, oldResult.Status);
        var only = Assert.Single(vm.Items);
        Assert.Equal("new", only.Id);
    }

    [Fact]
    public async Task SelectFilterByTag_Text_ShowsOnlyTextSends()
    {
        var vm = await LoadedVmAsync(new FakeSendUiService());

        vm.SelectFilterByTag("send:text");

        Assert.NotEmpty(vm.FilteredItems);
        Assert.All(vm.FilteredItems, i => Assert.Equal(SendType.Text, i.Type));
    }

    [Fact]
    public async Task CreateSendAsync_TextDraft_CallsServiceWithDraftAndAddsItem()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "一次性口令";
        draft.Text = "123456";

        var result = await vm.CreateSendAsync(draft, file: null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(draft, svc.LastCreateDraft);
        Assert.Null(svc.LastCreateFile);
        Assert.Contains(vm.Items, s => s.Name == "一次性口令" && s.Type == SendType.Text);
    }

    [Fact]
    public async Task CreateSendAsync_FileDraft_PassesSourceThrough()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "合同.zip";
        draft.FileName = "合同.zip";
        var source = MemorySource("合同.zip", new byte[] { 1, 2, 3 });

        var result = await vm.CreateSendAsync(draft, source, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(source, svc.LastCreateFile);
        Assert.Equal(SendType.File, svc.LastCreateDraft!.Type);
    }

    [Fact]
    public async Task CreateSendAsync_InvalidDraft_ReturnsFailure_AndDoesNotCallService()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var draft = SendEditorDraft.CreateDefault(SendType.Text); // empty name/text

        var result = await vm.CreateSendAsync(draft, file: null, CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Null(svc.LastCreateDraft);
    }

    [Fact]
    public async Task CreateSendAsync_KnownFailure_ReturnsFailed_AndKeepsReplacementFile()
    {
        // 保存失败(已知异常)必须返回 Failed 并保留 draft.ReplacementFile 供原样重试,不清集合、不泄露异常细节。
        var vm = new SendViewModel(new ThrowingSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "合同";
        draft.FileName = "合同.zip";
        var source = MemorySource("合同.zip", new byte[] { 1, 2, 3 });
        draft.ReplacementFile = source;

        var result = await vm.CreateSendAsync(draft, draft.ReplacementFile, CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Same(source, draft.ReplacementFile);
        Assert.True(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CreateSendAsync_FileChangedDuringUpload_ReturnsFailed_WithActionableMessage_AndKeepsReplacementFile()
    {
        var vm = new SendViewModel(new ThrowingInvalidDataSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "合同";
        draft.FileName = "合同.zip";
        var source = MemorySource("合同.zip", new byte[] { 1, 2, 3 });
        draft.ReplacementFile = source;

        var result = await vm.CreateSendAsync(draft, draft.ReplacementFile, CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal("文件在读取或上传过程中已被修改，请重新选择文件后重试。", result.Error);
        Assert.Same(source, draft.ReplacementFile);
        Assert.True(vm.HasError);
        Assert.Equal(result.Error, vm.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task UpdateSendFromDraftAsync_InvalidDraft_ReturnsFailure_AndDoesNotCallService()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var item = vm.Items.First(s => s.Type == SendType.Text);
        var draft = SendEditorDraft.CreateDefault(SendType.Text); // empty name/text

        var result = await vm.UpdateSendFromDraftAsync(item, draft, file: null, CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Null(svc.LastUpdateDraft);
    }

    [Fact]
    public async Task UpdateSendFromDraftAsync_ItemNotInCollection_ReturnsFailure()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var ghost = new SendListItem("nonexistent-id", "幽灵", SendType.Text, "无", "https://vault.example/#/send/ghost/seed");
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "改名";
        draft.Text = "内容";

        var result = await vm.UpdateSendFromDraftAsync(ghost, draft, file: null, CancellationToken.None);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task UpdateSendFromDraftAsync_ReplacesItem_AndPassesId()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var item = vm.Items.First(s => s.Type == SendType.Text);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "改名后";
        draft.Text = "新内容";

        var result = await vm.UpdateSendFromDraftAsync(item, draft, file: null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(item.Id, svc.LastUpdateId);
        Assert.Contains(vm.Items, s => s.Id == item.Id && s.Name == "改名后");
    }

    [Fact]
    public async Task UpdateSendFromDraftAsync_FileChangedDuringUpload_ReturnsFailed_WithActionableMessage_AndKeepsReplacementFile()
    {
        var vm = new SendViewModel(new ThrowingInvalidDataSendUiService());
        var item = new SendListItem("s1", "周报.pdf", SendType.File, "d", "https://vault.example/#/send/acc1/seed1");
        vm.Items.Add(item);
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "合同";
        draft.FileName = "合同.zip";
        var source = MemorySource("合同.zip", new byte[] { 1, 2, 3 });
        draft.ReplacementFile = source;

        var result = await vm.UpdateSendFromDraftAsync(item, draft, draft.ReplacementFile, CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal("文件在读取或上传过程中已被修改，请重新选择文件后重试。", result.Error);
        Assert.Same(source, draft.ReplacementFile);
        Assert.True(vm.HasError);
        Assert.Equal(result.Error, vm.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CreateSendAsync_CompensationFailure_HidesInternalSendIdAndDiagnostics()
    {
        const string orphanedSendId = "internal-send-id-91";
        const string diagnostic = "Bearer private-token from C:\\private\\payload.bin";
        var svc = new FakeSendUiService
        {
            CreateError = new Vault.SendUploadCompensationException(
                orphanedSendId,
                new IOException(diagnostic),
                new HttpRequestException(diagnostic)),
        };
        var vm = await LoadedVmAsync(svc);
        var draft = SendEditorDraft.CreateDefault(SendType.Text);
        draft.Name = "一次性口令";
        draft.Text = "123456";

        var result = await vm.CreateSendAsync(draft, file: null, CancellationToken.None);

        const string expected = "文件上传未完成，服务器可能保留了未完成的 Send。请刷新列表，确认并删除该条目后再重试。";
        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(expected, result.Error);
        Assert.Equal(expected, vm.Error);
        Assert.DoesNotContain(orphanedSendId, vm.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostic, vm.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_RequestTimeout_ReturnsFailedAndSetsErrorState()
    {
        var vm = new SendViewModel(new TimeoutSendUiService());

        var result = await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, result.Error);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.Error);
        Assert.Equal(DataPageState.Error, vm.LoadState);
    }

    [Fact]
    public async Task UpdateSendFromDraftAsync_ProtectedReplacementKeep_RequiresPasswordAndKeepsFile()
    {
        var service = new PasswordRequiredSendUiService();
        var vm = new SendViewModel(service);
        await vm.LoadAsync(CancellationToken.None);
        var item = Assert.Single(vm.Items);
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "replacement";
        draft.FileName = "replacement.bin";
        draft.ExistingHasPassword = true;
        var source = MemorySource("replacement.bin", [1, 2, 3]);
        draft.ReplacementFile = source;

        var result = await vm.UpdateSendFromDraftAsync(
            item,
            draft,
            draft.ReplacementFile,
            CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal("此 Send 已设置密码。替换文件时请重新输入原密码或设置新密码。", result.Error);
        Assert.Same(source, draft.ReplacementFile);
        Assert.Single(vm.Items);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CreateSendAsync_RequestTimeout_ReturnsFailedInsteadOfCancelled()
    {
        var vm = new SendViewModel(new TimeoutSendUiService());
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "合同";
        draft.FileName = "合同.zip";
        var source = MemorySource("合同.zip", [1, 2, 3]);

        var result = await vm.CreateSendAsync(draft, source, CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal("文件传输长时间没有进展，请检查连接后重试。", result.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task UpdateSendFromDraftAsync_UncertainOldDelete_LocksExactPairAndDoesNotInviteRetry()
    {
        var service = new UncertainReplacementSendUiService();
        var vm = new SendViewModel(service);
        await vm.LoadAsync(CancellationToken.None);
        var original = Assert.Single(vm.Items);
        var draft = SendEditorDraft.CreateDefault(SendType.File);
        draft.Name = "replacement.pdf";
        draft.FileName = "replacement.pdf";
        var source = MemorySource("replacement.pdf", [1, 2, 3]);

        var result = await vm.UpdateSendFromDraftAsync(
            original,
            draft,
            source,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, service.LoadCalls);
        Assert.Contains(vm.Items, item =>
            item.Id == "old-send"
            && item.ReconciliationState == SendReconciliationState.ReplacementOriginalPending
            && !item.CanModify);
        Assert.Contains(vm.Items, item =>
            item.Id == "new-send"
            && item.ReconciliationState == SendReconciliationState.ReplacementNewPending
            && !item.CanModify);
        Assert.True(vm.ShowNotice);
        Assert.Contains("已锁定", vm.Notice);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DeleteSendCommand_RemovesItemAndCallsService()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var item = vm.Items[0];

        vm.DeleteSendCommand.Execute(new SendDeleteRequest(
            item,
            TestSessionLeases.CreateSessionLease()));
        await WaitForIdle(vm);

        Assert.Equal(item.Id, svc.LastDeletedId);
        Assert.DoesNotContain(vm.Items, s => s.Id == item.Id);
        Assert.DoesNotContain(vm.FilteredItems, s => s.Id == item.Id);
    }

    [Fact]
    public async Task DeleteSendCommand_RecoverableFailure_ShowsSanitizedActionableError()
    {
        const string diagnostic = "DELETE https://vault.example/api/sends/secret?token=private";
        var svc = new FakeSendUiService
        {
            DeleteError = new HttpRequestException(diagnostic),
        };
        var vm = await LoadedVmAsync(svc);
        var item = vm.Items[0];
        var request = new SendDeleteRequest(item, TestSessionLeases.CreateSessionLease());

        await vm.DeleteSendCommand.ExecuteAsync(request);

        Assert.Equal(
            "无法确认删除结果，请刷新列表检查该 Send 是否仍存在后再操作。",
            vm.Error);
        Assert.DoesNotContain(diagnostic, vm.Error, StringComparison.Ordinal);
        Assert.True(vm.HasError);
        Assert.True(vm.ShowContent);
        Assert.False(vm.ShowError);
        Assert.Contains(vm.Items, send => send.Id == item.Id);
    }

    [Fact]
    public async Task DeleteSendCommand_UnknownFailure_PropagatesWithoutWritingError()
    {
        const string diagnostic = "invalid internal mutation state: private-token";
        var failure = new InvalidOperationException(diagnostic);
        var svc = new FakeSendUiService { DeleteError = failure };
        var vm = await LoadedVmAsync(svc);
        var request = new SendDeleteRequest(
            vm.Items[0],
            TestSessionLeases.CreateSessionLease());

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.DeleteSendCommand.ExecuteAsync(request));

        Assert.Same(failure, thrown);
        Assert.Null(vm.Error);
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task DeleteSendCommand_NullItem_NoOp()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        var count = vm.Items.Count;

        vm.DeleteSendCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Equal(count, vm.Items.Count);
        Assert.Null(svc.LastDeletedId);
    }

    [Fact]
    public async Task CopyLinkCommand_UsesServiceLinkAndSecretClipboard()
    {
        var clip = new RecordingClipboard();
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc, clip);
        var item = vm.Items.First(s => !string.IsNullOrEmpty(s.Link));

        vm.CopyLinkCommand.Execute(item);

        Assert.Equal(item.Link, clip.Text);
        Assert.Equal(1, clip.SecretCount);
        Assert.Equal(0, clip.PlainCount);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_TextSend_ShowsText()
    {
        var svc = new FakeSendUiService
        {
            ScriptedReceived = new SendReceivedResult(true, false, SendType.Text, "分享", "hello world", null, null, null),
        };
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/acc/seed";

        vm.OpenReceivedLinkCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Equal("https://vault.example/#/send/acc/seed", svc.LastOpenedUrl);
        Assert.Equal("hello world", vm.ReceivedText);
        Assert.True(vm.HasReceivedText);
        Assert.False(vm.ReceivedWrongPassword);
        Assert.Equal(string.Empty, vm.ReceivedLinkUrl);
        Assert.Equal(string.Empty, vm.ReceivedLinkPassword);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_PassesPassword_WhenProvided()
    {
        var svc = new FakeSendUiService
        {
            ScriptedReceived = new SendReceivedResult(true, false, SendType.Text, "n", "x", null, null, null),
        };
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/acc/seed";
        vm.ReceivedLinkPassword = "hunter2";

        vm.OpenReceivedLinkCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Equal("hunter2", svc.LastOpenedPassword);
        Assert.Equal(string.Empty, vm.ReceivedLinkUrl);
        Assert.Equal(string.Empty, vm.ReceivedLinkPassword);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_WrongPassword_SetsWrongPasswordFlag()
    {
        var svc = new FakeSendUiService { ScriptedReceived = SendReceivedResult.Wrong() };
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/acc/seed";

        vm.OpenReceivedLinkCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.True(vm.ReceivedWrongPassword);
        Assert.Null(vm.ReceivedText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_FileSend_SetsReceivedFileName()
    {
        var svc = new FakeSendUiService
        {
            ScriptedReceived = new SendReceivedResult(
                true,
                false,
                SendType.File,
                "报告",
                null,
                "报告.pdf",
                null,
                new Core.Models.SendAccessResult
                {
                    AccessId = "access",
                    Endpoints = Core.Models.ServerEndpoints.FromWebVault("https://vault.example"),
                    Seed = new byte[16],
                }),
        };
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/acc/seed";

        vm.OpenReceivedLinkCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Equal("报告.pdf", vm.ReceivedFileName);
        Assert.True(vm.HasReceivedFile);
        Assert.NotNull(vm.LastReceived);
        Assert.NotNull(vm.LastReceived!.Accessed);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_EmptyUrl_NoOp()
    {
        var svc = new FakeSendUiService();
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "   ";

        vm.OpenReceivedLinkCommand.Execute(null);
        await WaitForIdle(vm);

        Assert.Null(svc.LastOpenedUrl);
        Assert.Null(vm.ReceivedText);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_RecoverableFailure_ShowsSanitizedActionableError()
    {
        const string diagnostic = "Could not read C:\\private\\send.bin; token=private";
        var svc = new FakeSendUiService { OpenError = new IOException(diagnostic) };
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/access/seed";

        await vm.OpenReceivedLinkCommand.ExecuteAsync(null);

        Assert.Equal(
            "无法读取该 Send 的内容，请确认链接完整后重试。",
            vm.Error);
        Assert.DoesNotContain(diagnostic, vm.Error, StringComparison.Ordinal);
        Assert.True(vm.HasError);
        Assert.True(vm.ShowContent);
        Assert.False(vm.ShowError);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_OpaqueFailureResult_IsSanitized()
    {
        const string diagnostic = "server stack trace: access_token=private";
        var svc = new FakeSendUiService
        {
            ScriptedReceived = SendReceivedResult.Failure(diagnostic),
        };
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/access/seed";

        await vm.OpenReceivedLinkCommand.ExecuteAsync(null);

        Assert.Equal(
            "无法打开该 Send 链接，请检查链接是否完整，并稍后重试。",
            vm.Error);
        Assert.DoesNotContain(diagnostic, vm.Error, StringComparison.Ordinal);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_RequestTimeout_PreservesStableTimeoutMessage()
    {
        var vm = new SendViewModel(new TimeoutSendUiService())
        {
            ReceivedLinkUrl = "https://vault.example/#/send/access/seed",
        };

        await vm.OpenReceivedLinkCommand.ExecuteAsync(null);

        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.Error);
        Assert.True(vm.HasError);
        Assert.False(vm.ShowError);
    }

    [Fact]
    public async Task OpenReceivedLinkCommand_UnknownFailure_PropagatesWithoutWritingError()
    {
        const string diagnostic = "invalid internal receive state: private-token";
        var failure = new InvalidOperationException(diagnostic);
        var svc = new FakeSendUiService { OpenError = failure };
        var vm = await LoadedVmAsync(svc);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/access/seed";

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.OpenReceivedLinkCommand.ExecuteAsync(null));

        Assert.Same(failure, thrown);
        Assert.Null(vm.Error);
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ClearSensitive_WhileReceiveIsPending_DropsAndDisposesLateResult()
    {
        var service = new DeferredReceiveSendUiService();
        var vm = new SendViewModel(service)
        {
            ReceivedLinkUrl = "https://vault.example/#/send/access/seed",
        };
        vm.OpenReceivedLinkCommand.Execute(null);
        var execution = vm.OpenReceivedLinkCommand.ExecutionTask!;
        var accessed = AccessResult(7);

        vm.ClearSensitive();
        service.Completion.SetResult(new SendReceivedResult(
            true,
            false,
            SendType.File,
            "late",
            null,
            "late.bin",
            null,
            accessed));
        await execution;

        Assert.Null(vm.LastReceived);
        Assert.Null(vm.ReceivedFileName);
        Assert.Throws<ObjectDisposedException>(() => accessed.CopySeed());
    }

    [Fact]
    public void ReplacingOrClearingReceivedResult_DisposesOwnedAccessSeed()
    {
        var vm = new SendViewModel(new FakeSendUiService());
        var firstAccess = AccessResult(1);
        var secondAccess = AccessResult(2);
        vm.LastReceived = new SendReceivedResult(
            true, false, SendType.File, "first", null, "first.bin", null, firstAccess);

        vm.LastReceived = new SendReceivedResult(
            true, false, SendType.File, "second", null, "second.bin", null, secondAccess);

        Assert.Throws<ObjectDisposedException>(() => firstAccess.CopySeed());
        vm.ClearSensitive();
        Assert.Throws<ObjectDisposedException>(() => secondAccess.CopySeed());
    }

    [Fact]
    public async Task CancelReceive_RacingCompletion_IsIdempotentAndDoesNotCommitLateState()
    {
        var service = new DeferredReceiveSendUiService();
        var vm = new SendViewModel(service)
        {
            ReceivedLinkUrl = "https://vault.example/#/send/access/seed",
        };
        vm.OpenReceivedLinkCommand.Execute(null);
        var execution = vm.OpenReceivedLinkCommand.ExecutionTask!;
        var accessed = AccessResult(3);
        var firstCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var cancellation = Task.Run(() =>
        {
            vm.CancelReceive();
            firstCancellation.SetResult();
            for (var index = 1; index < 100; index++)
                vm.CancelReceive();
        });
        await firstCancellation.Task;
        service.Completion.TrySetResult(new SendReceivedResult(
            true, false, SendType.File, "late", null, "late.bin", null, accessed));

        await Task.WhenAll(cancellation, execution);
        Assert.Null(vm.LastReceived);
        Assert.Null(vm.ReceivedFileName);
        Assert.Throws<ObjectDisposedException>(() => accessed.CopySeed());
    }

    private static SendAccessResult AccessResult(byte value) => new()
    {
        AccessId = "access",
        Endpoints = ServerEndpoints.FromWebVault("https://vault.example"),
        Seed = Enumerable.Repeat(value, 16).ToArray(),
    };

    [Fact]
    public void ClearSensitive_CancelsLoadAndDropsAllSecretReferences()
    {
        var vm = new SendViewModel(new FakeSendUiService())
        {
            ReceivedLinkUrl = "https://vault.example/#/send/id/key",
            ReceivedLinkPassword = "secret",
            ReceivedText = "payload",
            ReceivedFileName = "secret.txt",
            LastReceived = SendReceivedResult.Wrong(),
            Notice = "replacement state",
        };

        ((ISensitiveState)vm).ClearSensitive();

        Assert.Equal(string.Empty, vm.ReceivedLinkUrl);
        Assert.Equal(string.Empty, vm.ReceivedLinkPassword);
        Assert.Null(vm.ReceivedText);
        Assert.Null(vm.ReceivedFileName);
        Assert.Null(vm.LastReceived);
        Assert.Null(vm.Notice);
        Assert.Empty(vm.Items);
        Assert.Empty(vm.FilteredItems);
        Assert.Equal(DataPageState.Loading, vm.LoadState);
    }

    private static async Task WaitForIdle(SendViewModel vm)
    {
        // 异步 RelayCommand 同步发起;让出线程直到命令完成(IsBusy 落回 false)。
        for (var i = 0; i < 50 && vm.IsBusy; i++)
            await Task.Yield();
    }

    private sealed class ThrowingSendUiService : ISendUiService
    {
        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<SendListItem>>(new HttpRequestException("network down"));
        public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default) =>
            Task.FromException<SendListItem>(new IOException("upload failed"));
        public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default) =>
            Task.FromException<SendListItem>(new IOException("upload failed"));
        public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default) =>
            Task.FromException(new IOException("delete failed"));
        public string? CopyShareLink(SendListItem item) => item.Link;
        public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default) =>
            Task.FromException<SendReceivedResult>(new IOException("access failed"));
        public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default) =>
            Task.FromException<SendEditorDraft?>(new IOException("edit failed"));
    }

    private sealed class ThrowingInvalidDataSendUiService : ISendUiService
    {
        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SendListItem>>(Array.Empty<SendListItem>());
        public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default) =>
            Task.FromException<SendListItem>(new InvalidDataException("file changed"));
        public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default) =>
            Task.FromException<SendListItem>(new InvalidDataException("file changed"));
        public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default) =>
            Task.CompletedTask;
        public string? CopyShareLink(SendListItem item) => item.Link;
        public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default) =>
            Task.FromException<SendReceivedResult>(new InvalidDataException("file changed"));
        public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default) =>
            Task.FromException<SendEditorDraft?>(new InvalidDataException("file changed"));
    }

    // latest-wins 专用替身:按调用顺序返回预置的加载任务,忽略其余成员。
    private sealed class DeferredSendUiService : ISendUiService
    {
        private readonly Queue<Task<IReadOnlyList<SendListItem>>> _loads;
        public DeferredSendUiService(params Task<IReadOnlyList<SendListItem>>[] loads) => _loads = new(loads);

        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) => _loads.Dequeue();
        public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default) => throw new NotSupportedException();
        public string? CopyShareLink(SendListItem item) => item.Link;
        public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class PasswordRequiredSendUiService : ISendUiService
    {
        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SendListItem>>(
                [new("protected-send", "original.bin", SendType.File, "d", null)]);
        public Task<SendListItem> CreateSendAsync(
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SendListItem> UpdateSendAsync(
            string id,
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) =>
            Task.FromException<SendListItem>(new Vault.SendPasswordRequiredForReplacementException());
        public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public string? CopyShareLink(SendListItem item) => item.Link;
        public Task<SendReceivedResult> OpenReceivedLinkAsync(
            string url,
            string? password,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SendEditorDraft?> GetEditDraftAsync(
            string id,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class TimeoutSendUiService : ISendUiService
    {
        private static TaskCanceledException Timeout() => new(
            "upload timed out",
            new RequestTimeoutException(
                RequestTimeoutPhase.UploadResponseHeaders,
                TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<SendListItem>>(Timeout());
        public Task<SendListItem> CreateSendAsync(
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => Task.FromException<SendListItem>(Timeout());
        public Task<SendListItem> UpdateSendAsync(
            string id,
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => Task.FromException<SendListItem>(Timeout());
        public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default) =>
            Task.FromException(Timeout());
        public string? CopyShareLink(SendListItem item) => item.Link;
        public Task<SendReceivedResult> OpenReceivedLinkAsync(
            string url,
            string? password,
            CancellationToken ct = default) => Task.FromException<SendReceivedResult>(Timeout());
        public Task<SendEditorDraft?> GetEditDraftAsync(
            string id,
            CancellationToken ct = default) => Task.FromException<SendEditorDraft?>(Timeout());
    }

    private sealed class DeferredReceiveSendUiService : ISendUiService
    {
        public TaskCompletionSource<SendReceivedResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SendListItem>>([]);
        public Task<SendListItem> CreateSendAsync(
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SendListItem> UpdateSendAsync(
            string id,
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public string? CopyShareLink(SendListItem item) => item.Link;
        public Task<SendReceivedResult> OpenReceivedLinkAsync(
            string url,
            string? password,
            CancellationToken ct = default) => Completion.Task;
        public Task<SendEditorDraft?> GetEditDraftAsync(
            string id,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UncertainReplacementSendUiService : ISendUiService
    {
        public int LoadCalls { get; private set; }

        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default)
        {
            LoadCalls++;
            IReadOnlyList<SendListItem> sends = LoadCalls == 1
                ? [new("old-send", "original.pdf", SendType.File, "d", null)]
                :
                [
                    new("old-send", "original.pdf", SendType.File, "d", null),
                    new("new-send", "replacement.pdf", SendType.File, "d", null),
                ];
            return Task.FromResult(sends);
        }

        public Task<SendListItem> CreateSendAsync(
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SendListItem> UpdateSendAsync(
            string id,
            SendEditorDraft draft,
            SendFileSource? file,
            CancellationToken ct = default) =>
            Task.FromException<SendListItem>(new Vault.SendReplacementUncertainException(
                id,
                new SendMutationReceipt(
                    "new-send",
                    null,
                    SendMutationKind.Replace,
                    "new-access",
                    ReplacedSendId: id),
                new HttpRequestException("response lost")));

        public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public string? CopyShareLink(SendListItem item) => item.Link;

        public Task<SendReceivedResult> OpenReceivedLinkAsync(
            string url,
            string? password,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SendEditorDraft?> GetEditDraftAsync(
            string id,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
