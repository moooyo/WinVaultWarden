using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SendWriteServiceTests
{
    private static readonly byte[] UserKeyBytes = Enumerable.Range(0, 64).Select(i => (byte)(i + 2)).ToArray();
    private static readonly ITransferIdleTimeoutPolicy NoIdleTimeout =
        new TransferIdleTimeoutPolicy(Timeout.InfiniteTimeSpan);

    private static (SendWriteService service, FakeSendApiClient api, SendCryptoService crypto, SymmetricCryptoKey userKey, VaultSessionLease lease) NewService(
        ITransferIdleTimeoutPolicy? timeoutPolicy = null,
        bool disableSend = false)
    {
        var userKey = new SymmetricCryptoKey(UserKeyBytes);
        var session = new VaultSession();
        var lease = session.BeginAccountSession("test-account", "test@test.com", new ServerOrigin("https://vault.test"));
        session.SetUnlockedKey(userKey);
        session.SetTokens("access-token", "refresh-token");
        if (disableSend)
        {
            session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
            {
                Organizations = [new VaultOrganization("org-1", "Team", 2, 2, true)],
                Policies =
                [
                    new VaultOrganizationPolicy(
                        "policy-1",
                        "org-1",
                        (int)OrganizationPolicyType.DisableSend,
                        true,
                        "{}"),
                ],
            });
        }
        var crypto = new SendCryptoService(new CryptoService());
        var api = new FakeSendApiClient();
        return (new SendWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(api),
            crypto,
            session,
            new FakeSendCompensationClient(),
            timeoutPolicy ?? NoIdleTimeout), api, crypto, userKey, lease);
    }

    private static SendDraftModel TextDraft(string? password = null, string? id = null, string? key = null) => new()
    {
        Id = id,
        Key = key,
        Type = SendType.Text,
        Name = "My Send",
        Notes = "note text",
        TextContent = "the secret",
        TextHidden = true,
        DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        ExpirationDate = DateTimeOffset.UtcNow.AddDays(3),
        MaxAccessCount = 4,
        Disabled = false,
        HideEmail = true,
        ExistingAuthType = id is null ? SendAuthType.Unknown : SendAuthType.None,
        ExistingRawAuthType = id is null ? int.MinValue : (int)SendAuthType.None,
        PasswordChange = password is null ? new SendPasswordChange.Keep() : new SendPasswordChange.Set(password),
    };

    private static SendFileSource MemorySource(string fileName, byte[] bytes) => new(
        fileName,
        bytes.LongLength,
        _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

    [Fact]
    public async Task SaveTextSend_New_EncryptsFieldsAndCreates()
    {
        var (service, api, _, _, lease) = NewService();

        var receipt = await service.SaveTextSendAsync(
            TextDraft(),
            lease,
            TestContext.Current.CancellationToken);

        Assert.Equal("create-text", Assert.Single(api.Calls));
        Assert.Equal(api.CreateTextResult.Id, receipt.SendId);
        Assert.Equal(SendMutationKind.Create, receipt.Kind);
        var req = api.LastCreateText!;
        Assert.Equal((int)SendType.Text, req.Type);
        // 字段必须是 EncString (type-2 前缀),绝不能是明文。
        Assert.StartsWith("2.", req.Name);
        Assert.NotEqual("My Send", req.Name);
        Assert.StartsWith("2.", req.Notes);
        Assert.NotNull(req.Text);
        Assert.StartsWith("2.", req.Text!.Text);
        Assert.NotEqual("the secret", req.Text.Text);
        Assert.True(req.Text.Hidden);
        // key 字段是 seed 用 userKey 包裹的 EncString。
        Assert.StartsWith("2.", req.Key);
        // deletionDate 必填,ISO8601 round-trip。
        Assert.False(string.IsNullOrEmpty(req.DeletionDate));
        Assert.NotNull(req.ExpirationDate);
        Assert.Equal(4, req.MaxAccessCount);
        Assert.True(req.HideEmail);
        Assert.Null(req.File);
        Assert.Null(req.FileLength);
    }

    [Fact]
    public async Task SaveTextSend_DisableSendPolicy_RejectsBeforeApiCall()
    {
        var (service, api, _, _, lease) = NewService(disableSend: true);

        await Assert.ThrowsAsync<OrganizationPolicyException>(() =>
            service.SaveTextSendAsync(
                TextDraft(),
                lease,
                TestContext.Current.CancellationToken));

        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task SaveTextSend_NoPassword_PasswordProofNull()
    {
        var (service, api, _, _, lease) = NewService();

        await service.SaveTextSendAsync(TextDraft(password: null), lease, TestContext.Current.CancellationToken);

        Assert.Null(api.LastCreateText!.Password);
    }

    [Fact]
    public async Task SaveTextSend_WithPassword_SendsProofNotPlaintext()
    {
        var (service, api, _, _, lease) = NewService();

        await service.SaveTextSendAsync(TextDraft(password: "hunter2"), lease, TestContext.Current.CancellationToken);

        var proof = api.LastCreateText!.Password;
        Assert.False(string.IsNullOrEmpty(proof));
        Assert.NotEqual("hunter2", proof);
        // 是 base64(PBKDF2 32 字节)。
        Assert.Equal(32, Convert.FromBase64String(proof!).Length);
    }

    [Fact]
    public async Task SaveTextSend_Existing_Updates()
    {
        var (service, api, crypto, userKey, lease) = NewService();
        var wrappedKey = crypto.WrapSeed(crypto.GenerateSeed(), userKey);

        var receipt = await service.SaveTextSendAsync(
            TextDraft(id: "s-7", key: wrappedKey),
            lease,
            TestContext.Current.CancellationToken);

        Assert.Equal("update", Assert.Single(api.Calls));
        Assert.Equal("s-7", receipt.SendId);
        Assert.Equal(SendMutationKind.Update, receipt.Kind);
        Assert.Equal("s-7", api.LastUpdateId);
        Assert.Equal((int)SendType.Text, api.LastUpdate!.Type);
    }

    [Fact]
    public async Task SaveTextSend_OfflineSnapshotFailsBeforeApiCall()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "test-account",
            "test@test.com",
            new ServerOrigin("https://vault.test"));
        session.SetUnlockedKey(new SymmetricCryptoKey(UserKeyBytes));
        session.SetSnapshot(
            new DecryptedVault(session.Account, [], [], 0, 0),
            dataStale: true);
        var api = new FakeSendApiClient();
        var service = new SendWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(api),
            new SendCryptoService(new CryptoService()),
            session,
            new FakeSendCompensationClient(),
            NoIdleTimeout);

        await Assert.ThrowsAsync<SendStateStaleException>(() =>
            service.SaveTextSendAsync(
                TextDraft(),
                lease,
                TestContext.Current.CancellationToken));

        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task SaveTextSend_V2UserKeyWritesType7Seed()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "test-account",
            "test@test.com",
            new ServerOrigin("https://vault.test"));
        var userKey = V2UserKey.Generate();
        session.SetUnlockedKey(userKey);
        session.SetTokens("access-token", "refresh-token");
        var api = new FakeSendApiClient
        {
            CreateTextResult = new SendResponseDto(
                "created-v2",
                "access-v2",
                0,
                "2.name",
                null,
                null,
                null,
                "7.key",
                null,
                0,
                null,
                (int)SendAuthType.None,
                false,
                false,
                DateTimeOffset.UnixEpoch,
                null,
                DateTimeOffset.UnixEpoch.AddDays(1),
                "send"),
        };
        var sendCrypto = new SendCryptoService(new CryptoService());
        var service = new SendWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(api),
            sendCrypto,
            session,
            new FakeSendCompensationClient(),
            NoIdleTimeout);

        await service.SaveTextSendAsync(
            TextDraft(),
            lease,
            TestContext.Current.CancellationToken);

        Assert.StartsWith("7.", api.LastCreateText!.Key);
        Assert.Equal(16, sendCrypto.UnwrapSeed(api.LastCreateText.Key, userKey).Length);
    }

    [Theory]
    [InlineData(SendAuthType.Email, 0)]
    [InlineData(SendAuthType.Unknown, 99)]
    [InlineData(SendAuthType.Unknown, int.MinValue)]
    public async Task SaveTextSend_UnsupportedAuthentication_FailsBeforeSessionOrApi(
        SendAuthType authType,
        int rawAuthType)
    {
        var api = new FakeSendApiClient();
        var service = new SendWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(api),
            new SendCryptoService(new CryptoService()),
            new VaultSession(),
            new FakeSendCompensationClient(),
            NoIdleTimeout);
        var foreignSession = new VaultSession();
        var foreignLease = foreignSession.BeginAccountSession(
            "foreign", "foreign@example.test", new ServerOrigin("https://foreign.example"));
        var draft = new SendDraftModel
        {
            Id = "existing-send",
            Type = SendType.Text,
            Name = "name",
            TextContent = "content",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
            ExistingHasPassword = authType == SendAuthType.Unknown,
            ExistingAuthType = authType,
            ExistingRawAuthType = rawAuthType,
        };

        var error = await Assert.ThrowsAsync<SendUnsupportedAuthenticationException>(() =>
            service.SaveTextSendAsync(draft, foreignLease, TestContext.Current.CancellationToken));

        Assert.Equal(authType, error.AuthType);
        Assert.Equal(rawAuthType, error.RawAuthType);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task CreateFileSend_EncryptsFieldsAndStreamsToReturnedUrl()
    {
        var (service, api, _, _, lease) = NewService();
        // SendFileUploadV2Response 是位置记录,须用位置构造函数语法。
        var sendResponse = new SendResponseDto(
            Id: "s-9", AccessId: "acc-9", Type: 1, Name: "", Notes: null,
            Text: null, File: new SendFileDto("f-2", "2.file", null, null),
            Key: null, MaxAccessCount: null, AccessCount: 0,
            Password: null, AuthType: (int)SendAuthType.None, Disabled: false, HideEmail: false,
            RevisionDate: null, ExpirationDate: null,
            DeletionDate: DateTimeOffset.UtcNow.AddDays(7), Object: null);
        api.CreateFileV2Result = new SendFileUploadV2Response(
            FileUploadType: FileUploadType.Direct,
            Object: null,
            Url: "/sends/s-9/file/f-2",
            SendResponse: sendResponse);
        var fileBytes = new byte[] { 10, 20, 30, 40, 50 };
        var draft = new SendDraftModel
        {
            Type = SendType.File,
            Name = "doc",
            FileName = "report.pdf",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        };

        var result = await service.CreateFileSendAsync(
            draft, MemorySource("report.pdf", fileBytes), lease, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "create-file-v2", "upload" }, api.Calls);
        Assert.Equal("s-9", result.SendId);
        Assert.Equal("acc-9", result.AccessId);
        Assert.Equal(SendMutationKind.Create, result.Kind);
        var req = api.LastCreateFileV2!;
        Assert.Equal((int)SendType.File, req.Type);
        Assert.NotNull(req.File);
        Assert.StartsWith("2.", req.File!.FileName);
        Assert.NotEqual("report.pdf", req.File.FileName);
        Assert.Null(req.Text);
        // fileLength == EncArrayBuffer 长度 == 1 + 16 + 32 + ceil 后的密文长度。
        Assert.NotNull(req.FileLength);
        Assert.Equal(api.LastUploadBuffer!.Length, req.FileLength);
        // upload 调用到的是 create 返回的 Url,文件名是加密后的 fileName。
        Assert.Equal("/sends/s-9/file/f-2", api.LastUploadUrl);
        Assert.Equal(FileUploadType.Direct, api.LastUploadType);
        Assert.Equal("s-9", api.LastUploadSendId);
        Assert.Equal("f-2", api.LastUploadFileId);
        Assert.Equal(req.File.FileName, api.LastUploadFileName);
        // 缓冲区头字节为 EncArrayBuffer 版本 2。
        Assert.Equal((byte)2, api.LastUploadBuffer![0]);
        Assert.True(api.LastUploadBuffer.Length >= 1 + 16 + 32);
    }

    [Fact]
    public async Task CreateFileSend_StalledDestination_UsesTransferWriteTimeout()
    {
        var policy = new TransferIdleTimeoutPolicy(TimeSpan.FromMilliseconds(30));
        var (service, api, _, _, lease) = NewService(policy);
        api.UploadAction = async (content, ct) =>
        {
            await using var destination = new BlockingWriteStream();
            await content.WriteAsync(destination, ct);
        };
        var draft = new SendDraftModel
        {
            Type = SendType.File,
            Name = "doc",
            FileName = "report.pdf",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        };

        var error = await Assert.ThrowsAsync<TransferIdleTimeoutException>(() =>
            service.CreateFileSendAsync(
                draft,
                MemorySource("report.pdf", [1, 2, 3]),
                lease,
                TestContext.Current.CancellationToken));

        Assert.Equal(TransferIoOperation.Write, error.Operation);
    }

    [Fact]
    public async Task UpdateFileSendMetadata_MissingWrappedKey_Throws()
    {
        // 没有原 wrapped key 无法复用 seed,必须显式失败而非静默用新 seed 破坏文件。
        var (service, _, _, _, lease) = NewService();
        var draft = new SendDraftModel
        {
            Id = "s-1",
            Type = SendType.File,
            Name = "x",
            FileName = "x.bin",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(3),
            ExistingAuthType = SendAuthType.None,
            ExistingRawAuthType = (int)SendAuthType.None,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateFileSendMetadataAsync(draft, lease, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateFileSend_WithExistingId_Throws()
    {
        // 创建入口只接受新草稿(Id 为空);带 Id 必须显式失败,避免误把替换当创建。
        var (service, _, _, _, lease) = NewService();
        var draft = new SendDraftModel
        {
            Id = "s-1",
            Type = SendType.File,
            Name = "x",
            FileName = "x.bin",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(3),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateFileSendAsync(draft, MemorySource("x.bin", new byte[] { 1 }), lease, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteSend_CallsDelete()
    {
        var (service, api, _, _, lease) = NewService();

        await service.DeleteSendAsync("s-3", lease, TestContext.Current.CancellationToken);

        Assert.Equal("delete:s-3", Assert.Single(api.Calls));
        Assert.Equal("s-3", api.LastDeleteId);
    }

    [Fact]
    public async Task SaveTextSend_WhenLocked_Throws()
    {
        var crypto = new SendCryptoService(new CryptoService());
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "locked", "locked@example.test", new ServerOrigin("https://vault.test"));
        var service = new SendWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(new FakeSendApiClient()),
            crypto,
            session,
            new FakeSendCompensationClient(),
            NoIdleTimeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveTextSendAsync(TextDraft(), lease, TestContext.Current.CancellationToken));
    }

    // Task 11: 更新既有文本/文件 Send 必须复用原 seed(通过 draft.Key 解包),绝不轮换 —— 否则服务端
    // 仍以旧 seed 加密的字段(文件名/文件字节)将永久无法解密。
    [Theory]
    [InlineData(SendType.Text)]
    [InlineData(SendType.File)]
    public async Task ExistingSend_ReusesOriginalSeed(SendType type)
    {
        var (service, api, crypto, userKey, lease) = NewService();
        var originalSeed = crypto.GenerateSeed();
        var draft = ExistingDraft(type, crypto.WrapSeed(originalSeed, userKey), new SendPasswordChange.Keep());

        if (type == SendType.Text)
            await service.SaveTextSendAsync(draft, lease, TestContext.Current.CancellationToken);
        else
            await service.UpdateFileSendMetadataAsync(draft, lease, TestContext.Current.CancellationToken);

        var sentSeed = crypto.UnwrapSeed(api.LastUpdate!.Key, userKey);
        Assert.Equal(originalSeed, sentSeed);
    }

    // Task 11: Keep/Set/Remove 是显式三态,分别映射为 null 密码证明(不改密码)/新证明(设置或替换)/
    // 专用 remove-password 路由(移除)。绝不能用 null/空字符串暗号代替。
    [Theory]
    [MemberData(nameof(PasswordCases))]
    public async Task PasswordChange_UsesExplicitServerSemantics(
        SendType type, SendPasswordChange change, bool expectProof, bool expectRemove)
    {
        var (service, api, crypto, userKey, lease) = NewService();
        var seed = crypto.GenerateSeed();
        var draft = ExistingDraft(type, crypto.WrapSeed(seed, userKey), change);

        if (type == SendType.Text)
            await service.SaveTextSendAsync(draft, lease, TestContext.Current.CancellationToken);
        else
            await service.UpdateFileSendMetadataAsync(draft, lease, TestContext.Current.CancellationToken);

        Assert.Equal(expectProof, api.LastUpdate!.Password is not null);
        Assert.Equal(expectRemove, api.Calls.Contains("remove-password"));
        if (change is SendPasswordChange.Set set)
            Assert.Equal(crypto.ComputePasswordProof(set.Password, seed), api.LastUpdate.Password);
    }

    public static TheoryData<SendType, SendPasswordChange, bool, bool> PasswordCases => new()
    {
        { SendType.Text, new SendPasswordChange.Keep(), false, false },
        { SendType.Text, new SendPasswordChange.Set("new-password"), true, false },
        { SendType.Text, new SendPasswordChange.Remove(), false, true },
        { SendType.File, new SendPasswordChange.Keep(), false, false },
        { SendType.File, new SendPasswordChange.Set("new-password"), true, false },
        { SendType.File, new SendPasswordChange.Remove(), false, true },
    };

    private static SendDraftModel ExistingDraft(SendType type, string wrappedKey, SendPasswordChange passwordChange) => new()
    {
        Id = "send-1",
        Key = wrappedKey,
        Type = type,
        Name = "Existing",
        TextContent = type == SendType.Text ? "secret" : null,
        FileName = type == SendType.File ? "existing.bin" : null,
        DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
        ExistingHasPassword = true,
        ExistingAuthType = SendAuthType.Password,
        ExistingRawAuthType = (int)SendAuthType.Password,
        PasswordChange = passwordChange,
    };

    private sealed class BlockingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) =>
            Task.Delay(Timeout.InfiniteTimeSpan, ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken ct = default) => new(Task.Delay(Timeout.InfiniteTimeSpan, ct));
    }
}
