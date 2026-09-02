using App.Services;
using App.ViewModels.Models;
using Api;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using System.Net;
using Xunit;
using AppSendType = App.ViewModels.Models.SendType;
using CoreSendType = Core.Enums.SendType;

namespace App.Tests;

public class RealSendUiServiceTests
{
    private static Vault.VaultSession NewUnlockedSession(out VaultSessionLease lease)
    {
        var session = new Vault.VaultSession();
        lease = session.BeginAccountSession(
            "test-account",
            "test@example.test",
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        return session;
    }

    private static Send CoreSend(string id, CoreSendType type, string name, bool hasPw = false) => new()
    {
        Id = id,
        AccessId = "acc-" + id,
        Type = type,
        Name = name,
        DeletionDate = DateTimeOffset.Parse("2026-07-10T00:00:00Z"),
        AuthType = hasPw ? SendAuthType.Password : SendAuthType.None,
        RawAuthType = hasPw ? (int)SendAuthType.Password : (int)SendAuthType.None,
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
    public async Task GetSendsAsync_StaleSnapshotIsVisibleButReadOnly()
    {
        var session = NewUnlockedSession(out var lease);
        session.SetSnapshot(
            new Vault.DecryptedVault(session.Account, [], [], 0, 0),
            dataStale: true);
        var read = new MutableSendService
        {
            Sends = [CoreSend("cached", CoreSendType.Text, "离线 Send")],
            SessionLeaseProvider = () => lease,
            IsStale = true,
        };
        var service = new SendUiService(
            read,
            new FakeSendWriteService(),
            new FakeSendAccessService(),
            session);

        var item = Assert.Single(await service.GetSendsAsync(CancellationToken.None));

        Assert.Equal("离线 Send", item.Name);
        Assert.True(item.IsReadOnly);
        Assert.False(item.CanModify);
        Assert.Null(await service.GetEditDraftAsync(item.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CreateSendAsync_TextDraft_CallsSaveTextWithMappedDraftAndAbsoluteDeletionDate()
    {
        var write = new FakeSendWriteService();
        var session = NewUnlockedSession(out var lease);
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.Text);
        draft.OriginatingSession = lease;
        draft.Name = "一次性口令";
        draft.Text = "123456";
        draft.HideTextByDefault = true;
        draft.DeletionDateLabel = "7 天";
        draft.MaxAccessCount = 3;
        draft.Disabled = true;
        draft.HideEmail = true;
        draft.Password = "pw";

        await svc.CreateSendAsync(draft, file: null, CancellationToken.None);

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
    public async Task CreateSendAsync_FileDraft_ForwardsReopenableSourceToWriteService()
    {
        var write = new FakeSendWriteService();
        var session = NewUnlockedSession(out var lease);
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = lease;
        draft.Name = "合同.zip";
        draft.FileName = "合同.zip";
        draft.ExistingHasPassword = true;
        draft.DeletionDateLabel = "30 天";
        var bytes = new byte[] { 1, 2, 3, 4 };
        var source = MemorySource("合同.zip", bytes);

        await svc.CreateSendAsync(draft, source, CancellationToken.None);

        Assert.Equal(CoreSendType.File, write.LastCreateDraft!.Type);
        Assert.False(write.LastCreateDraft.ExistingHasPassword);
        Assert.Equal("合同.zip", write.LastCreateDraft.FileName);
        // App 边界直接转发前层可重开源,绝不复制字节或另做包装。
        Assert.Same(source, write.LastCreateSource);
        Assert.Equal("合同.zip", write.LastCreateSource!.FileName);
        Assert.Equal(bytes.LongLength, write.LastCreateSource.Length);
        // 主契约要求可重开源：连续两次打开都得到相同内容。
        Assert.Equal(bytes, await ReadAll(write.LastCreateSource));
        Assert.Equal(bytes, await ReadAll(write.LastCreateSource));
    }

    [Fact]
    public void SendFileSource_ValidateLength_RejectsOverLimit_BeforeAnyOpen()
    {
        // App 选择文件时(StorageFileSendSource.CreateAsync)在构造可重开源、开流之前先按明文口径校验上限,
        // 故 >500 MiB 文件从不进入加密/上传。此处固化上限阈值与边界(恰好 500 MiB 允许)。
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SendFileSource.ValidateLength(SendFileSource.MaxPlaintextBytes + 1));
        SendFileSource.ValidateLength(SendFileSource.MaxPlaintextBytes);
    }

    private static SendFileSource MemorySource(string fileName, byte[] bytes) => new(
        fileName,
        bytes.LongLength,
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

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
        var session = NewUnlockedSession(out var lease);
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), session);

        await svc.DeleteSendAsync("s9", lease, CancellationToken.None);

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
        var accessed = new SendAccessResult
        {
            Type = CoreSendType.Text, Name = "分享", TextContent = "hello world",
            AccessId = "acc", Seed = new byte[16],
        };
        var access = new FakeSendAccessService
        {
            Result = accessed,
        };
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access);

        var r = await svc.OpenReceivedLinkAsync("https://vault.example/#/send/acc/seed", null, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.False(r.WrongPassword);
        Assert.Equal(AppSendType.Text, r.Type);
        Assert.Equal("hello world", r.TextContent);
        Assert.Null(r.Accessed);
        Assert.Throws<ObjectDisposedException>(() => accessed.CopySeed());
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

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, null)]
    [InlineData(HttpStatusCode.Forbidden, false, "无法访问该 Send，请确认链接有效且未过期。")]
    [InlineData(HttpStatusCode.InternalServerError, false, "Send 服务暂时不可用，请稍后再试。")]
    public async Task OpenReceivedLinkAsync_MapsOnlyApiUnauthorizedToWrongPassword(
        HttpStatusCode statusCode,
        bool expectedWrongPassword,
        string? expectedError)
    {
        var access = new FakeSendAccessService
        {
            Error = new VaultApiException(statusCode, null, "request failed"),
        };
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access);

        var result = await svc.OpenReceivedLinkAsync(
            "https://vault.example/#/send/acc/seed",
            "password",
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(expectedWrongPassword, result.WrongPassword);
        Assert.Equal(expectedError, result.Error);
        Assert.DoesNotContain("request failed", result.Error ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenReceivedLinkAsync_RecoverableFailure_ReturnsSanitizedError()
    {
        const string diagnostic = "Could not read C:\\private\\send.bin; token=private";
        var access = new FakeSendAccessService { Error = new IOException(diagnostic) };
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access);

        var result = await svc.OpenReceivedLinkAsync(
            "https://vault.example/#/send/acc/seed",
            null,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.WrongPassword);
        Assert.Equal("无法读取该 Send 的内容，请确认链接完整后重试。", result.Error);
        Assert.DoesNotContain(diagnostic, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenReceivedLinkAsync_UnknownFailure_Propagates()
    {
        const string diagnostic = "invalid internal access state: private-token";
        var failure = new InvalidOperationException(diagnostic);
        var access = new FakeSendAccessService { Error = failure };
        var svc = new SendUiService(new FakeSendService(), new FakeSendWriteService(), access);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.OpenReceivedLinkAsync(
                "https://vault.example/#/send/acc/seed",
                null,
                CancellationToken.None));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task OpenReceivedLinkAsync_CallerCancellation_RemainsCancellation()
    {
        var svc = new SendUiService(
            new FakeSendService(),
            new FakeSendWriteService(),
            new FakeSendAccessService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.OpenReceivedLinkAsync(
                "https://vault.example/#/send/acc/seed",
                null,
                cancellation.Token));
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
        var session = NewUnlockedSession(out var lease);
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = lease;
        draft.Name = "报告";
        draft.FileName = "报告.pdf";
        draft.WrappedKey = "2.origwrappedkey==";
        draft.DeletionDateLabel = "7 天";

        await svc.UpdateSendAsync("s-5", draft, file: null, CancellationToken.None);

        var saved = write.LastMetadataDraft!;
        Assert.Equal("s-5", saved.Id);
        Assert.Equal("2.origwrappedkey==", saved.Key);
        Assert.Equal(CoreSendType.File, saved.Type);
        // 未重选文件（file==null）绝不触发创建/替换，只更新元数据（不开流）。
        Assert.Null(write.LastCreateSource);
        Assert.Null(write.LastReplaceSource);
    }

    [Fact]
    public async Task UpdateSendAsync_UnchangedHydratedExpiration_PreservesExactAbsoluteValue()
    {
        var expiration = DateTimeOffset.Parse("2026-09-03T14:25:36.1234567+02:00");
        var existing = new Send
        {
            Id = "s-expiration",
            AccessId = "acc-expiration",
            Type = CoreSendType.Text,
            Name = "Expiration round trip",
            Text = new SendText("body", false),
            ExpirationDate = expiration,
            DeletionDate = DateTimeOffset.Parse("2026-09-20T00:00:00Z"),
            Key = "2.wrapped==",
        };
        var write = new FakeSendWriteService();
        var session = NewUnlockedSession(out var lease);
        var service = new SendUiService(
            new FakeSendService(existing),
            write,
            new FakeSendAccessService(),
            session);
        var draft = SendEditorDraft.FromExisting(existing);
        draft.OriginatingSession = lease;

        await service.UpdateSendAsync(existing.Id, draft, file: null, CancellationToken.None);

        Assert.Equal("自定义", draft.ExpirationDateLabel);
        Assert.Equal(expiration, write.LastTextDraft!.ExpirationDate);
        Assert.Equal(
            expiration.ToString("O"),
            write.LastTextDraft.ExpirationDate?.ToString("O"));
    }

    [Fact]
    public async Task UpdateSendAsync_FileDraft_WithNewSource_ReplacesExistingViaStream()
    {
        // 编辑既有文件 Send 且重选了文件 → 走 ReplaceFileSendAsync（建+传新件再删旧件），
        // App 边界直接转发前层可重开源，绝不把 null 当零字节，也不再走 byte[] 上传。
        var write = new FakeSendWriteService();
        var session = NewUnlockedSession(out var lease);
        var svc = new SendUiService(new FakeSendService(), write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = lease;
        draft.Name = "报告";
        draft.FileName = "报告-v2.pdf";
        draft.WrappedKey = "2.origwrappedkey==";
        draft.ExistingHasPassword = true;
        draft.DeletionDateLabel = "7 天";
        var bytes = new byte[] { 9, 8, 7, 6 };
        var source = MemorySource("报告-v2.pdf", bytes);

        await svc.UpdateSendAsync("s-5", draft, source, CancellationToken.None);

        Assert.Equal("s-5", write.LastReplaceId);
        Assert.Equal(CoreSendType.File, write.LastReplaceDraft!.Type);
        Assert.Equal("2.origwrappedkey==", write.LastReplaceDraft.Key);
        Assert.True(write.LastReplaceDraft.ExistingHasPassword);
        Assert.Same(source, write.LastReplaceSource);
        Assert.Equal("报告-v2.pdf", write.LastReplaceSource!.FileName);
        Assert.Equal(bytes.LongLength, write.LastReplaceSource.Length);
        Assert.Null(write.LastMetadataDraft);
    }

    [Fact]
    public async Task CreateFileSend_ConfirmedWriteWithRefreshFailure_ReturnsExactPendingItem()
    {
        var read = new MutableSendService { Error = new HttpRequestException("refresh failed") };
        var write = new FakeSendWriteService
        {
            CreateResult = new SendMutationReceipt(
                "committed-42", null, SendMutationKind.Create, "access-42"),
        };
        var session = NewUnlockedSession(out var lease);
        var service = new SendUiService(read, write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = lease;
        draft.Name = "Quarterly archive";
        draft.FileName = "archive.zip";

        var result = await service.CreateSendAsync(
            draft,
            MemorySource("archive.zip", [1, 2, 3]),
            CancellationToken.None);

        Assert.Equal("committed-42", result.Id);
        Assert.Equal(SendReconciliationState.CommittedPendingRefresh, result.ReconciliationState);
        Assert.False(result.CanModify);
        Assert.Equal(1, write.CreateCalls);
    }

    [Fact]
    public async Task CreateFileSend_RefreshLocatesOnlyExactCommittedId()
    {
        var read = new MutableSendService
        {
            Sends =
            [
                CoreSend("wrong-id", CoreSendType.File, "archive.zip"),
                CoreSend("committed-id", CoreSendType.File, "Quarterly archive"),
            ],
        };
        var write = new FakeSendWriteService
        {
            CreateResult = new SendMutationReceipt(
                "committed-id", null, SendMutationKind.Create, "access-id"),
        };
        var session = NewUnlockedSession(out var lease);
        read.SessionLeaseProvider = session.CaptureLease;
        var service = new SendUiService(read, write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = lease;
        draft.Name = "Quarterly archive";
        draft.FileName = "archive.zip";

        var result = await service.CreateSendAsync(
            draft,
            MemorySource("archive.zip", [4, 5, 6]),
            CancellationToken.None);

        Assert.Equal("committed-id", result.Id);
        Assert.Equal("Quarterly archive", result.Name);
        Assert.Equal(SendReconciliationState.None, result.ReconciliationState);
    }

    [Fact]
    public async Task UncertainReplacement_PersistsExactBlockedPairUntilOldDisappears()
    {
        var read = new MutableSendService
        {
            Sends =
            [
                CoreSend("old-send", CoreSendType.File, "Archive"),
                CoreSend("new-send", CoreSendType.File, "Archive"),
            ],
        };
        var write = new FakeSendWriteService
        {
            ReplaceError = new Vault.SendReplacementUncertainException(
                "old-send",
                new SendMutationReceipt(
                    "new-send",
                    null,
                    SendMutationKind.Replace,
                    "new-access",
                    ReplacedSendId: "old-send"),
                new HttpRequestException("response lost")),
        };
        var session = NewUnlockedSession(out var lease);
        read.SessionLeaseProvider = session.CaptureLease;
        var service = new SendUiService(read, write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = lease;
        draft.Name = "Archive";
        draft.FileName = "archive-v2.zip";

        var replacement = await service.UpdateSendAsync(
            "old-send",
            draft,
            MemorySource("archive-v2.zip", [7, 8, 9]),
            CancellationToken.None);
        var pending = await service.GetSendsAsync(CancellationToken.None);

        Assert.Equal("new-send", replacement.Id);
        Assert.Equal(SendReconciliationState.ReplacementNewPending, replacement.ReconciliationState);
        Assert.Equal(
            SendReconciliationState.ReplacementOriginalPending,
            pending.Single(item => item.Id == "old-send").ReconciliationState);
        Assert.Equal(
            SendReconciliationState.ReplacementNewPending,
            pending.Single(item => item.Id == "new-send").ReconciliationState);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteSendAsync("new-send", lease, CancellationToken.None));
        Assert.Null(await service.GetEditDraftAsync("old-send", CancellationToken.None));
        Assert.Null(write.LastDeletedId);

        read.Sends = [CoreSend("new-send", CoreSendType.File, "Archive")];
        var reconciled = Assert.Single(await service.GetSendsAsync(CancellationToken.None));
        Assert.Equal("new-send", reconciled.Id);
        Assert.True(reconciled.CanModify);
    }

    [Fact]
    public async Task UncertainReplacement_IsScopedToAccountAndServerEndpoints()
    {
        var read = new MutableSendService
        {
            Sends =
            [
                CoreSend("old-send", CoreSendType.File, "Archive"),
                CoreSend("new-send", CoreSendType.File, "Archive"),
            ],
        };
        var write = new FakeSendWriteService();
        var session = new Vault.VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.test",
            ServerEndpoints.ForSelfHosted("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        var accountALease = session.CaptureLease();
        read.SessionLeaseProvider = session.CaptureLease;
        write.ReplaceError = new Vault.SendReplacementUncertainException(
            "old-send",
            new SendMutationReceipt(
                "new-send",
                null,
                SendMutationKind.Replace,
                "new-access",
                SessionLease: session.CaptureLease(),
                ReplacedSendId: "old-send"),
            new HttpRequestException("response lost"));
        var service = new SendUiService(read, write, new FakeSendAccessService(), session);
        var draft = SendEditorDraft.CreateDefault(AppSendType.File);
        draft.OriginatingSession = accountALease;
        draft.Name = "Archive";
        draft.FileName = "archive-v2.zip";

        await service.UpdateSendAsync(
            "old-send",
            draft,
            MemorySource("archive-v2.zip", [1]),
            CancellationToken.None);

        session.BeginAccountSession(
            "account-b",
            "b@example.test",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        read.Sends = [CoreSend("old-send", CoreSendType.File, "Other account")];
        var otherAccountItem = Assert.Single(await service.GetSendsAsync(CancellationToken.None));

        Assert.Equal(SendReconciliationState.None, otherAccountItem.ReconciliationState);
        Assert.True(otherAccountItem.CanModify);
    }

    private sealed class FakeSendService : ISendService
    {
        private readonly Send[] _sends;
        public FakeSendService(params Send[] sends) => _sends = sends;
        public Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Send>>(_sends);
    }

    private sealed class MutableSendService : ISendService
    {
        public IReadOnlyList<Send> Sends { get; set; } = [];
        public Exception? Error { get; set; }
        public Func<Core.Session.VaultSessionLease>? SessionLeaseProvider { get; set; }
        public bool IsStale { get; set; }

        public Task<IReadOnlyList<Send>> GetSendsAsync(CancellationToken ct = default)
        {
            if (Error is not null)
                return Task.FromException<IReadOnlyList<Send>>(Error);
            return Task.FromResult(Sends);
        }

        public async Task<SendReadSnapshot> ObserveSendsAsync(CancellationToken ct = default)
        {
            var sends = await GetSendsAsync(ct);
            return new SendReadSnapshot(
                sends.Select(send => new SendObservation(
                    send.Id,
                    send.RevisionDate,
                    SendDecryptStatus.Decrypted,
                    send,
                    send.RawMutationWitness)).ToArray(),
                SessionLeaseProvider?.Invoke(),
                IsComplete: true,
                IsStale: IsStale);
        }
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
        public int CreateCalls { get; private set; }
        public SendMutationReceipt CreateResult { get; set; } =
            new("new", null, SendMutationKind.Create, "acc-new");
        public SendMutationReceipt ReplaceResult { get; set; } =
            new("new", null, SendMutationKind.Replace, "acc-new", ReplacedSendId: "old-send");
        public Exception? ReplaceError { get; set; }

        public Task<SendMutationReceipt> SaveTextSendAsync(
            SendDraftModel draft,
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        { LastTextDraft = draft; return Task.FromResult(new SendMutationReceipt(draft.Id ?? "new-text", null, draft.Id is null ? SendMutationKind.Create : SendMutationKind.Update, SessionLease: expectedSession)); }
        public Task<SendMutationReceipt> CreateFileSendAsync(
            SendDraftModel draft,
            SendFileSource file,
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        { CreateCalls++; LastCreateDraft = draft; LastCreateSource = file; return Task.FromResult(CreateResult with { SessionLease = expectedSession }); }
        public Task<SendMutationReceipt> UpdateFileSendMetadataAsync(
            SendDraftModel draft,
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        { LastMetadataDraft = draft; return Task.FromResult(new SendMutationReceipt(draft.Id!, null, SendMutationKind.Update, SessionLease: expectedSession)); }
        public Task<SendMutationReceipt> ReplaceFileSendAsync(
            string existingSendId,
            SendDraftModel draft,
            SendFileSource file,
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        {
            LastReplaceId = existingSendId;
            LastReplaceDraft = draft;
            LastReplaceSource = file;
            if (ReplaceError is Vault.SendReplacementUncertainException uncertain)
            {
                return Task.FromException<SendMutationReceipt>(
                    new Vault.SendReplacementUncertainException(
                        uncertain.ExistingSendId,
                        uncertain.Replacement with { SessionLease = expectedSession },
                        uncertain.InnerException ?? uncertain));
            }
            return ReplaceError is null
                ? Task.FromResult(ReplaceResult with
                {
                    SessionLease = expectedSession,
                    ReplacedSendId = existingSendId,
                })
                : Task.FromException<SendMutationReceipt>(ReplaceError);
        }
        public Task<SendMutationReceipt> DeleteSendAsync(
            string sendId,
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        { LastDeletedId = sendId; return Task.FromResult(new SendMutationReceipt(sendId, null, SendMutationKind.Delete, SessionLease: expectedSession)); }
    }

    private sealed class FakeSendAccessService : ISendAccessService
    {
        public SendAccessResult? Result { get; set; }
        public bool ThrowUnauthorized { get; set; }
        public Exception? Error { get; set; }
        public byte[] Downloaded { get; set; } = Array.Empty<byte>();

        public Task<SendAccessResult> AccessAsync(string shareUrl, string? password, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Error is not null)
                throw Error;
            if (ThrowUnauthorized)
                throw new UnauthorizedAccessException("send password invalid");
            return Task.FromResult(Result ?? new SendAccessResult
            {
                Type = Core.Enums.SendType.Text, Name = "n", AccessId = "acc", Seed = new byte[16],
            });
        }

        public async Task<long> DownloadFileAsync(
            SendDownloadLease download,
            Stream plaintextDestination,
            CancellationToken ct = default)
        {
            await plaintextDestination.WriteAsync(Downloaded, ct);
            return Downloaded.LongLength;
        }
    }
}
