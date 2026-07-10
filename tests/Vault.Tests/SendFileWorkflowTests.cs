using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
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
    private sealed record FileFixture(SendWriteService Service, FakeSendApiClient Api);

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
        var api = new FakeSendApiClient
        {
            CreateFileV2Result = new SendFileUploadV2Response(
                0,
                "send-fileUpload",
                "/sends/new-send/file/file-1",
                new SendResponseDto(
                    "new-send",
                    "access-new",
                    (int)SendType.File,
                    "2.name",
                    null,
                    null,
                    null,
                    "2.key",
                    null,
                    0,
                    null,
                    0,
                    false,
                    false,
                    null,
                    null,
                    DateTimeOffset.UtcNow.AddDays(7),
                    "send")),
        };
        return new FileFixture(
            new SendWriteService(
                FakeVaultApiClientProvider.ForAuthenticated<ISendApiClient>(api),
                new SendCryptoService(new CryptoService()),
                session),
            api);
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

    private static SendDraftModel ExistingFileDraft()
    {
        var crypto = new SendCryptoService(new CryptoService());
        using var userKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)(i + 3)).ToArray());
        return new SendDraftModel
        {
            Id = "old-send",
            Key = crypto.WrapSeed(crypto.GenerateSeed(), userKey),
            Type = SendType.File,
            Name = "File",
            FileName = "a.bin",
            DeletionDate = DateTimeOffset.UtcNow.AddDays(7),
            PasswordChange = new SendPasswordChange.Keep(),
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
            fixture.Service.CreateFileSendAsync(FileDraft(), source, CancellationToken.None));

        Assert.Equal(0, opens);
        Assert.Empty(fixture.Api.Calls);
    }

    [Fact]
    public async Task CreateFile_HappyPath_ReturnsServerIdsAndStreamsEncryptedContent()
    {
        var fixture = NewFileFixture();

        var result = await fixture.Service.CreateFileSendAsync(
            FileDraft(), MemorySource("a.bin", [10, 20, 30, 40, 50]), CancellationToken.None);

        Assert.Equal(["create-file-v2", "upload"], fixture.Api.Calls);
        Assert.Equal("new-send", result.SendId);
        Assert.Equal("access-new", result.AccessId);
        Assert.False(result.ReplacedExisting);
        var req = fixture.Api.LastCreateFileV2!;
        Assert.Equal((int)SendType.File, req.Type);
        // 文件名是用 seed 派生 cryptoKey 加密的 EncString（type-2 密文，非明文）。
        Assert.StartsWith("2.", req.File!.FileName);
        Assert.NotEqual("a.bin", req.File.FileName);
        Assert.Equal(fixture.Api.LastUploadFileName, req.File.FileName);
        // fileLength 必须精确等于实际流式上传的 EncArrayBuffer 字节数，供服务端逐字节比对。
        Assert.Equal(fixture.Api.LastUploadBuffer!.Length, req.FileLength);
        Assert.Equal((byte)2, fixture.Api.LastUploadBuffer![0]);
        Assert.Equal("/sends/new-send/file/file-1", fixture.Api.LastUploadUrl);
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
            fixture.Service.CreateFileSendAsync(FileDraft(), source, CancellationToken.None));

        Assert.Equal(["create-file-v2", "upload", "delete:new-send"], fixture.Api.Calls);
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
                caller.Token));

        Assert.Equal(
            ["create-file-v2", "upload", "delete:new-send"],
            fixture.Api.Calls);
        Assert.False(fixture.Api.DeleteTokenWasCanceled);
        Assert.Equal("new-send", fixture.Api.LastDeleteId);
    }

    [Fact]
    public async Task CleanupFailure_ThrowsActionableCompensationException()
    {
        var fixture = NewFileFixture();
        fixture.Api.UploadThrows = new IOException("upload failed");
        fixture.Api.DeleteThrows = new HttpRequestException("cleanup failed");

        var ex = await Assert.ThrowsAsync<SendUploadCompensationException>(() =>
            fixture.Service.CreateFileSendAsync(
                FileDraft(),
                MemorySource("a.bin", [1, 2, 3]),
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
            CancellationToken.None);

        Assert.Equal(
            ["create-file-v2", "upload", "delete:old-send"],
            fixture.Api.Calls);
        Assert.True(result.ReplacedExisting);
    }

    [Fact]
    public async Task MetadataUpdate_DoesNotCreateOrUpload()
    {
        var fixture = NewFileFixture();

        await fixture.Service.UpdateFileSendMetadataAsync(
            ExistingFileDraft(),
            CancellationToken.None);

        Assert.Equal(["update"], fixture.Api.Calls);
    }

    [Fact]
    public async Task Replace_OldDeleteFailure_RollsBackNewSend()
    {
        var fixture = NewFileFixture();
        fixture.Api.DeleteFailures["old-send"] =
            new HttpRequestException("old delete failed");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Service.ReplaceFileSendAsync(
                "old-send",
                ExistingFileDraft(),
                MemorySource("new.bin", [7, 8, 9]),
                CancellationToken.None));

        Assert.Equal(
            [
                "create-file-v2",
                "upload",
                "delete:old-send",
                "delete:new-send",
            ],
            fixture.Api.Calls);
    }
}
