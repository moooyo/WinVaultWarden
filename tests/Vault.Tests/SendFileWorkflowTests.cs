using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using System.Net;
using Vault;
using Xunit;

namespace Vault.Tests;

// Task 12：文件型 Send 的创建/元数据更新/替换工作流与失败补偿事务。
// 关键不变式：
//   - 明文上限（500 MiB）在任何 API/开流之前校验；
//   - 创建 = v2 建记录 → streaming 上传；上传失败/取消用【独立】15s 令牌删除孤儿记录；
//     清理删除若也失败，抛可操作的 SendUploadCompensationException（暴露孤儿 SendId，不泄露密钥）；
//   - 替换 = 先建+传新 Send，再删旧；删旧失败则回滚删新，任何失败绝不伪装成功；
//   - 元数据更新只发 PUT，绝不开流/上传。
public class SendFileWorkflowTests
{
    private static readonly ITransferIdleTimeoutPolicy NoIdleTimeout =
        new TransferIdleTimeoutPolicy(Timeout.InfiniteTimeSpan);

    private sealed record FileFixture(
        SendWriteService Service,
        FakeSendApiClient Api,
        FakeSendCompensationClient Compensation,
        VaultSession Session,
        AccessTokenBinding ExpectedBinding,
        VaultSessionLease Lease);

    private static FileFixture NewFileFixture()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a", "a@example.com", new ServerOrigin("https://a.example"));
        var key = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)(i + 3)).ToArray());
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(key);
            session.SetTokens("access-a", "refresh-a");
        });
        // 操作开始、lease 仍 current 时应被 SendWriteService 冻结的 binding(补偿删除必须用它)。
        var expectedBinding = session.GetAccessTokenBinding(lease)!;
        var api = new FakeSendApiClient
        {
            CreateFileV2Authorization = expectedBinding,
            CreateFileV2Result = new SendFileUploadV2Response(
                FileUploadType.Direct,
                "send-fileUpload",
                "/sends/new-send/file/file-1",
                new SendResponseDto(
                    Id: "new-send",
                    AccessId: "access-new",
                    Type: (int)SendType.File,
                    Name: "2.name",
                    Notes: null,
                    Text: null,
                    File: new SendFileDto("file-1", "2.file", null, null),
                    Key: "2.key",
                    MaxAccessCount: null,
                    AccessCount: 0,
                    Password: null,
                    AuthType: (int)SendAuthType.None,
                    Disabled: false,
                    HideEmail: false,
                    RevisionDate: null,
                    ExpirationDate: null,
                    DeletionDate: DateTimeOffset.UtcNow.AddDays(7),
                    Object: "send")),
        };
        var compensation = new FakeSendCompensationClient();
        return new FileFixture(
            new SendWriteService(
                FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(api),
                new SendCryptoService(new CryptoService()),
                session,
                compensation,
                NoIdleTimeout),
            api,
            compensation,
            session,
            expectedBinding,
            lease);
    }

    private static SendFileSource MemorySource(string fileName, byte[] bytes) => new(
        fileName,
        bytes.LongLength,
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

    private static SendDraftModel FileDraft() => new()
    {
        Type = SendType.File,
        Name = "File",
        FileName = "a.bin",
        DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        PasswordChange = new SendPasswordChange.Keep(),
    };

    private static SendDraftModel ExistingFileDraft(
        SendPasswordChange? passwordChange = null,
        bool existingHasPassword = false,
        SendAuthType? existingAuthType = null,
        int? existingRawAuthType = null,
        string? id = "old-send")
    {
        var crypto = new SendCryptoService(new CryptoService());
        using var userKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)(i + 3)).ToArray());
        var authType = existingAuthType
            ?? (existingHasPassword ? SendAuthType.Password : SendAuthType.None);
        return new SendDraftModel
        {
            Id = id,
            Key = crypto.WrapSeed(crypto.GenerateSeed(), userKey),
            Type = SendType.File,
            Name = "File",
            FileName = "a.bin",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
            ExistingHasPassword = existingHasPassword,
            ExistingAuthType = authType,
            ExistingRawAuthType = existingRawAuthType ?? (int)authType,
            PasswordChange = passwordChange ?? new SendPasswordChange.Keep(),
        };
    }

    [Fact]
    public async Task CreateFile_OverLimit_MakesNoApiCallOrOpen()
    {
        var fixture = NewFileFixture();
        var opens = 0;
        var source = new SendFileSource(
            "too-large.bin",
            500L * 1024 * 1024 + 1,
            _ =>
            {
                opens++;
                return ValueTask.FromResult<Stream>(Stream.Null);
            });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(), source, fixture.Lease, CancellationToken.None));

        Assert.Equal(0, opens);
        Assert.Empty(fixture.Api.Calls);
    }

    [Fact]
    public async Task CreateFile_StaleExpectedSession_CancelsBeforeApiOrFileOpen()
    {
        var fixture = NewFileFixture();
        var opens = 0;
        var source = new SendFileSource(
            "file.bin",
            1,
            _ =>
            {
                opens++;
                return ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false));
            });
        fixture.Session.BeginAccountSession(
            "account-b",
            "b@example.test",
            ServerEndpoints.ForSelfHosted("https://b.example"));
        fixture.Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(),
                source,
                fixture.Lease,
                TestContext.Current.CancellationToken));

        Assert.True(error.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, opens);
        Assert.Empty(fixture.Api.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different-send")]
    public async Task Replace_DraftIdMustExactlyMatchExistingId_BeforeAnySideEffect(string? draftId)
    {
        var fixture = NewFileFixture();
        var opens = 0;
        var source = new SendFileSource(
            "file.bin",
            1,
            _ =>
            {
                opens++;
                return ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false));
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(id: draftId),
                source,
                fixture.Lease,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, opens);
        Assert.Empty(fixture.Api.Calls);
        Assert.Empty(fixture.Compensation.DeletedSendIds);
    }

    [Fact]
    public async Task CreateFile_HappyPath_ReturnsServerIdsAndStreamsEncryptedContent()
    {
        var fixture = NewFileFixture();

        var result = await fixture.Service.CreateFileSendAsync(
            FileDraft(),
            MemorySource("a.bin", [10, 20, 30, 40, 50]),
            fixture.Lease,
            CancellationToken.None);

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        Assert.Equal("new-send", result.SendId);
        Assert.Equal("access-new", result.AccessId);
        Assert.Equal(SendMutationKind.Create, result.Kind);
        var req = fixture.Api.LastCreateFileV2!;
        Assert.Equal((int)SendType.File, req.Type);
        // 文件名是用 seed 派生 cryptoKey 加密的 EncString（type-2 密文，非明文）。
        Assert.StartsWith("2.", req.File!.FileName);
        Assert.NotEqual("a.bin", req.File.FileName);
        Assert.Equal(fixture.Api.LastUploadFileName, req.File.FileName);
        // fileLength 必须精确等于实际流式上传的 EncArrayBuffer 字节数，供服务端逐字节比对。
        Assert.Equal(fixture.Api.LastUploadBuffer!.Length, req.FileLength);
        Assert.Equal((byte)2, fixture.Api.LastUploadBuffer![0]);
        Assert.Equal(FileUploadType.Direct, fixture.Api.LastUploadType);
        Assert.Equal("/sends/new-send/file/file-1", fixture.Api.LastUploadUrl);
        Assert.Equal("new-send", fixture.Api.LastUploadSendId);
        Assert.Equal("file-1", fixture.Api.LastUploadFileId);
    }

    [Fact]
    public async Task CreateFile_ContentChangedBetweenPasses_FailsClosedAndCompensates()
    {
        // 安全门禁:文件在两遍加密之间被改成【相同长度但不同内容】。第二遍重算 MAC 侦测到不一致后抛错,
        // 创建流程 fail-closed:用独立令牌补偿删除刚建的孤儿记录,绝不留下 MAC/密文不匹配的损坏 Send。
        var fixture = NewFileFixture();
        var first = new byte[] { 1, 2, 3, 4 };
        var second = new byte[] { 4, 3, 2, 1 }; // 同长度,不同内容
        var opens = 0;
        var source = new SendFileSource("mutating.bin", first.LongLength, _ =>
        {
            opens++;
            var payload = opens == 1 ? first : second;
            return ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false));
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(), source, fixture.Lease, CancellationToken.None));

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        // 补偿删除走独立补偿客户端(冻结 binding + 独立令牌),不再经普通 ISendApiClient 的无鉴权 delete。
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
    }

    [Fact]
    public async Task CreateFile_UnknownTransport_CompensatesCreatedSendWithoutReadingBody()
    {
        var fixture = NewFileFixture();
        fixture.Api.CreateFileV2Result = fixture.Api.CreateFileV2Result with
        {
            FileUploadType = (FileUploadType)42,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(),
                MemorySource("unknown.bin", [1, 2, 3]),
                fixture.Lease,
                TestContext.Current.CancellationToken));

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        Assert.Null(fixture.Api.LastUploadBuffer);
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
        Assert.Equal(fixture.ExpectedBinding, fixture.Compensation.LastBinding);
    }

    [Fact]
    public async Task UploadFailure_DeletesCreatedRecordWithIndependentToken()
    {
        var fixture = NewFileFixture();
        fixture.Api.UploadThrows = new IOException("upload failed");
        using var caller = new CancellationTokenSource();
        fixture.Api.BeforeUploadFailure = caller.Cancel;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(),
                MemorySource("a.bin", [1, 2, 3]),
                fixture.Lease,
                caller.Token));

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        // 补偿删除用【捕获的冻结 binding】+【未取消的独立令牌】——即便调用方令牌已取消(BeforeUploadFailure)。
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
        Assert.False(fixture.Compensation.LastTokenWasCanceled);
        Assert.Equal(fixture.ExpectedBinding, fixture.Compensation.LastBinding);
    }

    [Fact]
    public async Task CleanupFailure_ThrowsActionableCompensationException()
    {
        var fixture = NewFileFixture();
        fixture.Api.UploadThrows = new IOException("upload failed");
        fixture.Compensation.ThrowFor["new-send"] = new HttpRequestException("cleanup failed");

        var ex = await Assert.ThrowsAsync<SendUploadCompensationException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(),
                MemorySource("a.bin", [1, 2, 3]),
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal("new-send", ex.OrphanedSendId);
        Assert.Contains("创建未完成且可能需要清理", ex.Message);
    }

    [Fact]
    public async Task Replace_CreatesUploadsThenDeletesOld()
    {
        var fixture = NewFileFixture();

        var result = await fixture.Service.ReplaceFileSendAsync(
            "old-send",
            ExistingFileDraft(),
            MemorySource("new.bin", [4, 5, 6]),
            fixture.Lease,
            CancellationToken.None);

        Assert.Equal(
            ["create-file-v2", "upload", "delete:old-send"],
            fixture.Api.Calls);
        // 删旧走普通 ISendApiClient(正常业务删除);替换成功不触发任何补偿。
        Assert.Empty(fixture.Compensation.DeletedSendIds);
        Assert.Equal(SendMutationKind.Replace, result.Kind);
        Assert.Equal("old-send", result.ReplacedSendId);
        Assert.Null(fixture.Api.LastCreateFileV2!.Password);
    }

    [Fact]
    public async Task Replace_ProtectedKeep_FailsBeforeApiOrFileOpen()
    {
        var fixture = NewFileFixture();
        var opens = 0;
        var source = new SendFileSource(
            "new.bin",
            3,
            _ =>
            {
                opens++;
                return ValueTask.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false));
            });

        await Assert.ThrowsAsync<SendPasswordRequiredForReplacementException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(existingHasPassword: true),
                source,
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal(0, opens);
        Assert.Empty(fixture.Api.Calls);
        Assert.Empty(fixture.Compensation.DeletedSendIds);
    }

    [Fact]
    public async Task Replace_ProtectedSet_CreatesPasswordProtectedReplacement()
    {
        var fixture = NewFileFixture();
        const string plaintextPassword = "replacement-password";

        await fixture.Service.ReplaceFileSendAsync(
            "old-send",
            ExistingFileDraft(
                new SendPasswordChange.Set(plaintextPassword),
                existingHasPassword: true),
            MemorySource("new.bin", [4, 5, 6]),
            fixture.Lease,
            CancellationToken.None);

        var proof = fixture.Api.LastCreateFileV2!.Password;
        Assert.NotNull(proof);
        Assert.NotEqual(plaintextPassword, proof);
        Assert.Equal(32, Convert.FromBase64String(proof!).Length);
        Assert.Equal(["create-file-v2", "upload", "delete:old-send"], fixture.Api.Calls);
    }

    [Fact]
    public async Task MetadataUpdate_DoesNotCreateOrUpload()
    {
        var fixture = NewFileFixture();

        await fixture.Service.UpdateFileSendMetadataAsync(
            ExistingFileDraft(existingHasPassword: true),
            fixture.Lease,
            CancellationToken.None);

        Assert.Equal(["update"], fixture.Api.Calls);
        Assert.Null(fixture.Api.LastUpdate!.Password);
    }

    [Theory]
    [InlineData(SendAuthType.Email, 0)]
    [InlineData(SendAuthType.Unknown, 99)]
    [InlineData(SendAuthType.Unknown, int.MinValue)]
    public async Task UnsupportedAuthentication_BlocksMetadataAndReplacementWithoutSideEffects(
        SendAuthType authType,
        int rawAuthType)
    {
        var metadataFixture = NewFileFixture();
        var metadataDraft = ExistingFileDraft(
            existingHasPassword: authType == SendAuthType.Unknown,
            existingAuthType: authType,
            existingRawAuthType: rawAuthType);

        var metadataError = await Assert.ThrowsAsync<SendUnsupportedAuthenticationException>(() =>
            metadataFixture.Service.UpdateFileSendMetadataAsync(
                metadataDraft,
                metadataFixture.Lease,
                TestContext.Current.CancellationToken));

        Assert.Equal(authType, metadataError.AuthType);
        Assert.Empty(metadataFixture.Api.Calls);

        var replacementFixture = NewFileFixture();
        var opens = 0;
        var source = new SendFileSource(
            "new.bin",
            1,
            _ =>
            {
                opens++;
                return ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false));
            });

        var replacementError = await Assert.ThrowsAsync<SendUnsupportedAuthenticationException>(() =>
            replacementFixture.Service.ReplaceFileSendAsync(
                "old-send",
                metadataDraft,
                source,
                replacementFixture.Lease,
                TestContext.Current.CancellationToken));

        Assert.Equal(authType, replacementError.AuthType);
        Assert.Equal(0, opens);
        Assert.Empty(replacementFixture.Api.Calls);
        Assert.Empty(replacementFixture.Compensation.DeletedSendIds);
    }

    [Fact]
    public async Task Replace_OldDeleteDefinitelyRejected_RollsBackNewSend()
    {
        var fixture = NewFileFixture();
        fixture.Api.DeleteFailures["old-send"] =
            new SendDeleteDefinitelyNotAppliedException(
                "old-send",
                HttpStatusCode.Forbidden,
                "Delete denied");

        await Assert.ThrowsAsync<SendDeleteDefinitelyNotAppliedException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(),
                MemorySource("new.bin", [7, 8, 9]),
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal(
            ["create-file-v2", "upload", "delete:old-send"],
            fixture.Api.Calls);
        // 删旧失败 → 用【冻结 binding + 独立令牌】回滚删新件(经补偿客户端,非普通 api)。
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
        Assert.False(fixture.Compensation.LastTokenWasCanceled);
        Assert.Equal(fixture.ExpectedBinding, fixture.Compensation.LastBinding);
    }

    [Fact]
    public async Task Replace_OldDeleteTransportFailure_PreservesNewAndReportsUncertainOutcome()
    {
        var fixture = NewFileFixture();
        fixture.Api.DeleteFailures["old-send"] =
            new SendDeleteOutcomeUncertainException(
                "old-send",
                new HttpRequestException("Connection closed after dispatch."));

        var error = await Assert.ThrowsAsync<SendReplacementUncertainException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(),
                MemorySource("new.bin", [7, 8, 9]),
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal("old-send", error.ExistingSendId);
        Assert.Equal("new-send", error.Replacement.SendId);
        Assert.Equal(
            ["create-file-v2", "upload", "delete:old-send"],
            fixture.Api.Calls);
        Assert.Empty(fixture.Compensation.DeletedSendIds);
    }

    [Fact]
    public async Task Replace_CancellationBeforeOldDeleteDispatch_RollsBackNewSend()
    {
        var fixture = NewFileFixture();
        using var caller = new CancellationTokenSource();
        fixture.Api.AfterUploadSuccess = caller.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(),
                MemorySource("new.bin", [7, 8, 9]),
                fixture.Lease,
                caller.Token));

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
        Assert.False(fixture.Compensation.LastTokenWasCanceled);
    }

    [Fact]
    public async Task UploadFailure_CompensatesWithBindingUsedBySuccessfulMetadataCreate()
    {
        var fixture = NewFileFixture();
        var refreshedBinding = fixture.ExpectedBinding with { Token = "refreshed-access" };
        fixture.Api.CreateFileV2Authorization = refreshedBinding;
        fixture.Api.UploadThrows = new IOException("upload failed");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(),
                MemorySource("a.bin", [1, 2, 3]),
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal(refreshedBinding, fixture.Compensation.LastBinding);
        Assert.NotEqual(fixture.ExpectedBinding.Token, fixture.Compensation.LastBinding!.Token);
    }

    // 竞态 A(创建):上传成功后、方法返回前 lease 失效(vault 被 Lock)。post-upload 的 ThrowIfExpired
    // 必须与 upload 处于同一补偿 try —— 抛 OperationCanceledException 前用【独立未取消令牌】删掉刚建的
    // 孤儿新件,绝不能把新 Send 遗留在服务端。RED(修复前):post-upload 校验在 try 外 → 只有 create/upload、
    // 无 delete:new(少补偿)。
    [Fact]
    public async Task CreateFile_LeaseExpiresAfterUpload_CompensatesNewWithIndependentToken()
    {
        var fixture = NewFileFixture();
        fixture.Api.AfterUploadSuccess = () => fixture.Session.Lock();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(),
                MemorySource("a.bin", [1, 2, 3]),
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        // post-upload lease 失效 → 用【冻结 binding + 未取消独立令牌】补偿删新件(经补偿客户端)。
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
        Assert.False(fixture.Compensation.LastTokenWasCanceled);
        Assert.Equal(fixture.ExpectedBinding, fixture.Compensation.LastBinding);
    }

    // 竞态 B(替换):建+传新件成功,删旧成功后、返回前 lease 失效。远端 replacement 已提交(旧件已删),
    // 此时【绝不能】补偿删新件 —— 否则新旧双删 = 数据丢失。宁可调用方抛 OperationCanceledException,
    // 下次同步看到新件。RED(修复前):删旧成功后的 ThrowIfExpired 在 delete catch 内 → 误删 new(多补偿)。
    [Fact]
    public async Task Replace_LeaseExpiresAfterDeletingOld_DoesNotDeleteNew()
    {
        var fixture = NewFileFixture();
        fixture.Api.AfterDeleteSuccess["old-send"] = () => fixture.Session.Lock();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(),
                MemorySource("new.bin", [4, 5, 6]),
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal(["create-file-v2", "upload", "delete:old-send"], fixture.Api.Calls);
        // 删旧【成功】后 lease 失效:replacement 已提交,绝不回删新件。补偿客户端一无所记。
        Assert.Empty(fixture.Compensation.DeletedSendIds);
    }

    // 竞态 C(替换,删旧前失效):替换的"建新件"阶段上传成功后 lease 立即失效。此时旧件尚未删,
    // 补偿只回滚新件,并且绝不触碰旧件(delete:old-send 永不发生)。
    [Fact]
    public async Task Replace_LeaseExpiresAfterUpload_RollsBackNewAndSkipsDeleteOld()
    {
        var fixture = NewFileFixture();
        fixture.Api.AfterUploadSuccess = () => fixture.Session.Lock();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(),
                MemorySource("new.bin", [7, 8, 9]),
                fixture.Lease,
                CancellationToken.None));

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        Assert.DoesNotContain("delete:old-send", fixture.Api.Calls);
        // 删旧【前】lease 已失效:回滚删新件(补偿客户端),绝不触碰旧件。
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
    }

    // 竞态 D(替换,inner 完成与"删旧前"校验之间失效):建+传新件成功,inner CreateFileCore 的 post-upload
    // lease 校验【已通过并返回】,但在 outer "删旧前" ThrowIfExpired 执行【之前】vault 被 Lock。此刻旧件仍在、
    // 新件已建;pre-delete 校验发现 lease 失效必须【回滚新件】(经补偿客户端删 new-send),且绝不触碰旧件——
    // 否则留下 orphan new(新件孤儿 + 旧件仍在)。补偿删除走独立补偿客户端(冻结 binding + 未取消独立令牌),
    // 不再经普通 ISendApiClient 的无鉴权 delete —— 故断言落在 Compensation.DeletedSendIds 上。
    //
    // 用手动 SynchronizationContext + 可控 upload gate 制造确定性竞态:分两次 pump continuation,在 inner
    // continuation 跑完(post-upload 校验通过、CreateFileCore 返回)之后、outer pre-delete continuation 执行
    // 之前插入 Lock。驱动期间把线程 SynchronizationContext 置为中性(null),使每个后继 continuation 被 Post
    // 回队列而非内联级联,从而每次 RunOne 仅推进一个 continuation,完全确定、单线程。
    [Fact]
    public void Replace_LeaseExpiresBetweenInnerCompletionAndPreDeleteCheck_RollsBackNewAndSkipsDeleteOld()
    {
        var fixture = NewFileFixture();
        var uploadGate = new TaskCompletionSource();
        fixture.Api.UploadGate = uploadGate.Task;

        var original = SynchronizationContext.Current;
        var pump = new ManualPumpSynchronizationContext();
        Task replaceTask;
        try
        {
            // 以 pump 为当前上下文启动:phase 1 同步跑到 upload 并阻塞在 gate(所有前置 await 均已完成 → 无排队)。
            SynchronizationContext.SetSynchronizationContext(pump);
            replaceTask = fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(),
                MemorySource("new.bin", [4, 5, 6]),
                fixture.Lease,
                CancellationToken.None);
            Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
            Assert.False(replaceTask.IsCompleted);
            Assert.Equal(0, pump.PendingCount);

            // 切到中性上下文,使随后每个 Task 完成时其 continuation 被 Post 回 pump(而非内联级联执行)。
            SynchronizationContext.SetSynchronizationContext(null);
            uploadGate.SetResult();
            Assert.True(pump.RunOne());   // upload 收尾并完成 → 排队 inner CreateFileCore 的 post-upload continuation
            Assert.True(pump.RunOne());   // post-upload 校验通过、CreateFileCore 返回 → 排队 outer 的"删旧前"continuation

            // 此刻 inner 已完成,outer pre-delete continuation 已排队但尚未执行;lease 仍有效。
            Assert.False(replaceTask.IsCompleted);
            Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
            Assert.Equal(1, pump.PendingCount);

            // 在 inner 完成与 outer pre-delete 校验之间 Lock,使 lease 失效。
            fixture.Session.Lock();

            // pump outer pre-delete continuation:ThrowIfExpired 发现 lease 失效并触发补偿。
            pump.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        // 方法以 OperationCanceledException 收场(async 方法抛 OCE → Task 进入 Canceled 状态)。
        Assert.True(replaceTask.IsCanceled);
        // 目标:pre-delete 校验失败必须回滚新件(经补偿客户端:冻结 binding + 独立未取消令牌),且绝不触碰旧件。
        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        Assert.DoesNotContain("delete:old-send", fixture.Api.Calls);
        Assert.Equal(["new-send"], fixture.Compensation.DeletedSendIds);
        Assert.False(fixture.Compensation.LastTokenWasCanceled);
        Assert.Equal(fixture.ExpectedBinding, fixture.Compensation.LastBinding);
    }

    // 单线程手动泵:Post 仅入队;RunOne 执行队首一个 continuation。测试驱动期间保持线程上下文为中性(null),
    // 使被执行的 continuation 内完成的任何 Task 的后继 continuation 都被 Post 回本队列(内联优化条件是
    // 目标上下文 == 当前上下文),从而每次 RunOne 精确推进一个 continuation。
    private sealed class ManualPumpSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback callback, object? state)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public int PendingCount => _queue.Count;

        public bool RunOne()
        {
            if (_queue.Count == 0)
                return false;
            var (callback, state) = _queue.Dequeue();
            callback(state);
            return true;
        }

        public void Drain()
        {
            while (RunOne())
            {
            }
        }
    }
}
