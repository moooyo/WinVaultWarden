using System.Security.Cryptography;
using System.Text;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class AttachmentServiceTests
{
    private readonly CryptoService _crypto = new();
    private readonly AttachmentCryptoService _attCrypto;
    private readonly VaultDecryptor _decryptor;
    private readonly SymmetricCryptoKey _userKey = new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    public AttachmentServiceTests()
    {
        _attCrypto = new AttachmentCryptoService(_crypto);
        _decryptor = new VaultDecryptor(_crypto);
    }

    private static readonly ITransferIdleTimeoutPolicy NoIdleTimeout =
        new TransferIdleTimeoutPolicy(Timeout.InfiniteTimeSpan);

    private sealed class FakeCompensationClient : IAttachmentCompensationClient
    {
        public List<(AccessTokenBinding Binding, string CipherId, string AttachmentId)> Deletes { get; } = [];
        public Exception? DeleteThrows { get; set; }
        public bool LastTokenWasCancelled { get; private set; }

        public Task DeleteAsync(
            AccessTokenBinding binding,
            string cipherId,
            string attachmentId,
            CancellationToken ct = default)
        {
            LastTokenWasCancelled = ct.IsCancellationRequested;
            Deletes.Add((binding, cipherId, attachmentId));
            return DeleteThrows is null ? Task.CompletedTask : Task.FromException(DeleteThrows);
        }
    }

    private AttachmentService CreateService(
        IVaultApiClientProvider clients,
        VaultSession session,
        ISyncService sync,
        IAttachmentCompensationClient? compensation = null) =>
        new(
            clients,
            _attCrypto,
            _decryptor,
            session,
            sync,
            compensation ?? new FakeCompensationClient(),
            NoIdleTimeout);

    private static ReopenableFileSource Source(string fileName, byte[] bytes) =>
        new(
            fileName,
            bytes.LongLength,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));

    // 把 sync 后的整库快照塞进真实 VaultSession,使 service 能从 session.Ciphers 读回附件。
    private sealed class FakeSync : ISyncService
    {
        private readonly VaultSession _session;
        private readonly DecryptedVault? _snapshot;
        public int Calls { get; private set; }
        public Exception? Throws { get; set; }
        public Action? BeforeResult { get; set; }

        public FakeSync(VaultSession session, DecryptedVault? snapshot)
        {
            _session = session;
            _snapshot = snapshot;
        }

        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            BeforeResult?.Invoke();
            if (Throws is not null)
                return Task.FromException<IReadOnlyList<Cipher>>(Throws);
            if (_snapshot is not null)
                _session.SetSnapshot(_snapshot);
            return Task.FromResult(_snapshot?.Ciphers ?? (IReadOnlyList<Cipher>)Array.Empty<Cipher>());
        }

        public Task<IReadOnlyList<Cipher>> SyncAsync(
            Core.Session.VaultSessionLease lease, CancellationToken ct = default) => SyncAsync(ct);
    }

    private VaultSession NewUnlockedSession()
    {
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(_userKey);
        return session;
    }

    private static DecryptedVault SnapshotWith(Cipher cipher)
        => new(
            new AccountInfo("test-account", "me@example.com", new ServerOrigin("https://vault.example"), "M", string.Empty),
            Array.Empty<Folder>(),
            new[] { cipher },
            SkippedCipherCount: 0,
            SkippedFolderCount: 0);

    private static Cipher CipherWithAttachments(string id, params CipherAttachment[] attachments)
        => new()
        {
            Id = id,
            Type = CipherType.Login,
            Name = "Item",
            Attachments = attachments,
        };

    // 构造一个携带"新格式"附件的 CipherDto(att.Key 用 itemKey 包裹)。
    private CipherDto CipherDtoWithNewFormatAttachment(
        string cipherId, string attId, string fileName, byte[] plaintext, string downloadUrl,
        SymmetricCryptoKey itemKey, string? cipherKeyEnc, out byte[] encryptedBuffer)
    {
        var attKey = _attCrypto.GenerateAttachmentKey();
        encryptedBuffer = _attCrypto.EncryptFile(plaintext, attKey);
        var encName = _attCrypto.EncryptFileName(fileName, attKey);
        var wrapped = _attCrypto.WrapKey(attKey, itemKey);
        var att = new CipherAttachmentDto(
            Id: attId, FileName: encName, Key: wrapped,
            Size: encryptedBuffer.LongLength.ToString(), SizeName: "5 B", Url: downloadUrl);
        return BuildCipherDto(cipherId, cipherKeyEnc, att);
    }

    private static CipherDto BuildCipherDto(string cipherId, string? cipherKeyEnc, params CipherAttachmentDto[] attachments)
        => new(
            Id: cipherId, Type: (int)CipherType.Login, Name: null, Notes: null,
            Key: cipherKeyEnc, OrganizationId: null, FolderId: null, Favorite: false, Reprompt: 0,
            Login: new LoginDto(null, null, null, null), Card: null, Identity: null,
            SecureNote: null, SshKey: null, Fields: null,
            CreationDate: null, RevisionDate: null, DeletedDate: null)
        {
            Attachments = attachments.Length == 0 ? null : attachments,
        };

    [Fact]
    public async Task Upload_WrapsWithItemKey_UploadsToReturnedUrl_AndReturnsRefreshedList()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-1", cipherKeyEnc: null),
            CreateResult = new AttachmentUploadV2Response(
                AttachmentId: "att-1",
                Url: "/ciphers/c-1/attachment/att-1",
                FileUploadType: FileUploadType.Direct),
        };
        var snapshot = SnapshotWith(CipherWithAttachments("c-1",
            new CipherAttachment("att-1", "report.pdf", 12345, "12 KB")));
        var sync = new FakeSync(session, snapshot);
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, sync);
        var plaintext = Encoding.UTF8.GetBytes("hello attachment");

        var result = await service.UploadAsync("c-1", Source("report.pdf", plaintext), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "get-cipher", "create-v2", "upload" }, api.Calls);
        Assert.Equal(1, sync.Calls);
        // create 请求:key 是 type-2 EncString;fileName 是加密后的(非明文);fileSize == buffer 长度。
        var req = api.LastCreateRequest!;
        Assert.StartsWith("2.", req.Key);
        Assert.StartsWith("2.", req.FileName);
        Assert.NotEqual("report.pdf", req.FileName);
        Assert.Equal(api.LastUploadBuffer!.LongLength, req.FileSize);
        // upload 到 create 返回的 Url,文件名是加密后的 fileName,缓冲首字节为 EncArrayBuffer 版本 2。
        Assert.Equal(FileUploadType.Direct, api.LastUploadType);
        Assert.Equal("/ciphers/c-1/attachment/att-1", api.LastUploadUrl);
        Assert.Equal("c-1", api.LastUploadCipherId);
        Assert.Equal("att-1", api.LastUploadAttachmentId);
        Assert.Equal(req.FileName, api.LastUploadFileName);
        Assert.Equal((byte)2, api.LastUploadBuffer![0]);
        // 返回的是重同步后 session 里该 cipher 的附件列表。
        var att = Assert.Single(result);
        Assert.Equal("att-1", att.Id);
        Assert.Equal("report.pdf", att.FileName);
    }

    [Fact]
    public async Task Upload_WithCipherKey_WrapsUnderItemKeyNotUserKey()
    {
        var session = NewUnlockedSession();
        var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var cipherKeyEnc = _crypto.Encrypt(itemKey.FullKey, _userKey).ToString();
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-2", cipherKeyEnc),
        };
        var snapshot = SnapshotWith(CipherWithAttachments("c-2"));
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, new FakeSync(session, snapshot));

        await service.UploadAsync("c-2", Source("f.bin", [1, 2, 3]), TestContext.Current.CancellationToken);

        // 包裹用 itemKey:用 itemKey 能 unwrap 出 attKey,用 userKey 则 MAC 失败。
        var wrapped = api.LastCreateRequest!.Key;
        var attKey = _attCrypto.UnwrapKey(wrapped, itemKey);
        Assert.Equal(64, attKey.FullKey.Length);
        Assert.Throws<CryptographicException>(() => _attCrypto.UnwrapKey(wrapped, _userKey));
    }

    [Fact]
    public async Task Upload_OverLimit_ThrowsAttachmentTooLargeException()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient { GetCipherResult = BuildCipherDto("c-3", null) };
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, new FakeSync(session, null));
        var tooBig = new ReopenableFileSource(
            "big.bin",
            AttachmentService.MaxPlaintextBytes + 1,
            _ => throw new InvalidOperationException("Oversized sources must not be opened."));

        var ex = await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
            service.UploadAsync("c-3", tooBig, TestContext.Current.CancellationToken));

        Assert.Equal(tooBig.Length, ex.ActualBytes);
        Assert.Equal(AttachmentService.MaxPlaintextBytes, ex.MaxBytes);
        // 超限发生在加密/网络之前:不应有任何 API 调用。
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Download_UnwrapsAttKey_RoundTripsPlaintext()
    {
        var session = NewUnlockedSession();
        var plaintext = Encoding.UTF8.GetBytes("round trip me");
        var dto = CipherDtoWithNewFormatAttachment(
            "c-4", "att-4", "doc.txt", plaintext, "https://files.example/att-4",
            itemKey: _userKey, cipherKeyEnc: null, out var encryptedBuffer);
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = dto,
            DownloadBytes = encryptedBuffer,
        };
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, new FakeSync(session, null));

        await using var destination = new MemoryStream();
        var written = await service.DownloadAsync("c-4", "att-4", destination, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "get-cipher", "download" }, api.Calls);
        Assert.Equal("https://files.example/att-4", api.LastDownloadUrl);
        Assert.Equal(plaintext.Length, written);
        Assert.Equal(plaintext, destination.ToArray());
    }

    [Fact]
    public async Task Download_LegacyFormat_NoAttKey_UsesItemKey()
    {
        var session = NewUnlockedSession();
        var plaintext = Encoding.UTF8.GetBytes("legacy body");
        // 旧格式:att.Key 为空,文件体直接用 itemKey(=userKey) 加密。
        var encryptedBuffer = _attCrypto.EncryptFile(plaintext, _userKey);
        var encName = _attCrypto.EncryptFileName("legacy.bin", _userKey);
        var att = new CipherAttachmentDto(
            Id: "att-legacy", FileName: encName, Key: null,
            Size: encryptedBuffer.LongLength.ToString(), SizeName: "11 B",
            Url: "https://files.example/att-legacy");
        var dto = BuildCipherDto("c-5", cipherKeyEnc: null, att);
        var api = new FakeAttachmentApiClient { GetCipherResult = dto, DownloadBytes = encryptedBuffer };
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, new FakeSync(session, null));

        await using var destination = new MemoryStream();
        await service.DownloadAsync("c-5", "att-legacy", destination, TestContext.Current.CancellationToken);

        Assert.Equal(plaintext, destination.ToArray());
    }

    [Fact]
    public async Task Download_AttachmentNotFound_Throws()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient { GetCipherResult = BuildCipherDto("c-6", null) };
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, new FakeSync(session, null));

        await using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAsync("c-6", "missing", destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_CallsDeleteAndResyncs_ReturnsRefreshedList()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient();
        var snapshot = SnapshotWith(CipherWithAttachments("c-7",
            new CipherAttachment("att-keep", "keep.txt", 7, "7 B")));
        var sync = new FakeSync(session, snapshot);
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, sync);

        var result = await service.DeleteAsync("c-7", "att-gone", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "delete" }, api.Calls);
        Assert.Equal("c-7", api.LastDeleteCipherId);
        Assert.Equal("att-gone", api.LastDeleteAttachmentId);
        Assert.Equal(1, sync.Calls);
        var att = Assert.Single(result);
        Assert.Equal("att-keep", att.Id);
    }

    [Fact]
    public async Task Operations_WhenLocked_Throw()
    {
        var session = new VaultSession(); // 未解锁,UserKey 为 null
        var api = new FakeAttachmentApiClient { GetCipherResult = BuildCipherDto("c-8", null) };
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, new FakeSync(session, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync("c-8", Source("f", [1]), TestContext.Current.CancellationToken));
        await using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAsync("c-8", "a", destination, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync("c-8", "a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MaxPlaintextBytes_IsHundredMegabytes_AndMatchesCoreLimit()
    {
        Assert.Equal(100L * 1024 * 1024, Core.Services.AttachmentLimits.MaxPlaintextBytes);
        Assert.Equal(Core.Services.AttachmentLimits.MaxPlaintextBytes, AttachmentService.MaxPlaintextBytes);
    }

    [Fact]
    public async Task Upload_WhenDataUploadFails_DeletesOrphanAndRethrowsOriginal()
    {
        var session = NewUnlockedSession();
        var boom = new InvalidOperationException("network died mid-upload");
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-1", cipherKeyEnc: null),
            CreateResult = new AttachmentUploadV2Response(
                AttachmentId: "att-orphan",
                Url: "/ciphers/c-1/attachment/att-orphan",
                FileUploadType: FileUploadType.Direct),
            UploadThrows = boom,
        };
        var sync = new FakeSync(session, null);
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            sync,
            compensation);

        // 第二步上传抛异常 → 原始异常按原样重抛(不被包成 VaultRefreshException)。
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync("c-1", Source("report.pdf", Encoding.UTF8.GetBytes("data")), TestContext.Current.CancellationToken));
        Assert.Same(boom, ex);

        // 触发一次补偿删除,针对第一步 create 返回的 attachmentId。
        Assert.Equal(new[] { "get-cipher", "create-v2", "upload" }, api.Calls);
        var deleted = Assert.Single(compensation.Deletes);
        Assert.Same(api.CreateAuthorization, deleted.Binding);
        Assert.Equal("c-1", deleted.CipherId);
        Assert.Equal("att-orphan", deleted.AttachmentId);
        // 上传失败后不应再重同步。
        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task Upload_WhenCancelledDuringDataUpload_StillUsesIndependentCompensationToken()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-2", cipherKeyEnc: null),
            CreateResult = new AttachmentUploadV2Response(
                AttachmentId: "att-2",
                Url: "/ciphers/c-2/attachment/att-2",
                FileUploadType: FileUploadType.Direct),
            UploadThrows = new OperationCanceledException(),
        };
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null),
            compensation);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.UploadAsync("c-2", Source("f.bin", [1, 2, 3]), TestContext.Current.CancellationToken));

        var deleted = Assert.Single(compensation.Deletes);
        Assert.Equal("att-2", deleted.AttachmentId);
        Assert.False(compensation.LastTokenWasCancelled);
    }

    [Fact]
    public async Task Upload_UnknownTransport_CompensatesCreatedAttachmentWithoutReadingBody()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-unknown", cipherKeyEnc: null),
            CreateResult = new AttachmentUploadV2Response(
                AttachmentId: "att-unknown",
                Url: "https://files.example/upload",
                FileUploadType: (FileUploadType)42),
        };
        var compensation = new FakeCompensationClient();
        var sync = new FakeSync(session, null);
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            sync,
            compensation);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.UploadAsync(
                "c-unknown",
                Source("file.bin", [1, 2, 3]),
                TestContext.Current.CancellationToken));

        Assert.Equal(["get-cipher", "create-v2", "upload"], api.Calls);
        Assert.Null(api.LastUploadBuffer);
        var deleted = Assert.Single(compensation.Deletes);
        Assert.Equal("c-unknown", deleted.CipherId);
        Assert.Equal("att-unknown", deleted.AttachmentId);
        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task Upload_LeaseExpiresAfterDataUpload_CompensatesBeforeRemoteCommitPoint()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-lease", cipherKeyEnc: null),
            CreateResult = new AttachmentUploadV2Response(
                AttachmentId: "att-lease",
                Url: "/ciphers/c-lease/attachment/att-lease",
                FileUploadType: FileUploadType.Direct),
            AfterUploadSuccess = () => session.Lock(),
        };
        var sync = new FakeSync(session, null);
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            sync,
            compensation);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.UploadAsync(
                "c-lease",
                Source("lease.bin", [1, 2, 3]),
                TestContext.Current.CancellationToken));

        var deleted = Assert.Single(compensation.Deletes);
        Assert.Equal("c-lease", deleted.CipherId);
        Assert.Equal("att-lease", deleted.AttachmentId);
        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task Upload_SyncCancellationAfterCommitPoint_ReturnsRefreshFailureWithoutCompensation()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-committed", cipherKeyEnc: null),
            CreateResult = new AttachmentUploadV2Response(
                AttachmentId: "att-committed",
                Url: "/ciphers/c-committed/attachment/att-committed",
                FileUploadType: FileUploadType.Direct),
        };
        using var caller = new CancellationTokenSource();
        var sync = new FakeSync(session, null)
        {
            BeforeResult = caller.Cancel,
            Throws = new OperationCanceledException(caller.Token),
        };
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            sync,
            compensation);

        var error = await Assert.ThrowsAsync<VaultRefreshException>(() =>
            service.UploadAsync(
                "c-committed",
                Source("committed.bin", [4, 5, 6]),
                caller.Token));

        Assert.True(caller.IsCancellationRequested);
        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Contains("已保存", error.Message);
        Assert.Contains("手动同步", error.Message);
        Assert.Empty(compensation.Deletes);
        Assert.Equal(1, sync.Calls);
        Assert.Equal(["get-cipher", "create-v2", "upload"], api.Calls);
    }

    [Fact]
    public async Task Upload_WhenCompensatingDeleteAlsoFails_ThrowsTypedCleanupError()
    {
        var session = NewUnlockedSession();
        var original = new TimeoutException("upload timed out");
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-3", cipherKeyEnc: null),
            CreateResult = new AttachmentUploadV2Response(
                AttachmentId: "att-3",
                Url: "/ciphers/c-3/attachment/att-3",
                FileUploadType: FileUploadType.Direct),
            UploadThrows = original,
        };
        var cleanupError = new InvalidOperationException("compensating delete also failed");
        var compensation = new FakeCompensationClient { DeleteThrows = cleanupError };
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null),
            compensation);

        var ex = await Assert.ThrowsAsync<AttachmentUploadCompensationException>(() =>
            service.UploadAsync("c-3", Source("f.bin", [1]), TestContext.Current.CancellationToken));
        Assert.Equal("c-3", ex.CipherId);
        Assert.Equal("att-3", ex.OrphanedAttachmentId);
        var errors = Assert.IsType<AggregateException>(ex.InnerException).InnerExceptions;
        Assert.Contains(original, errors);
        Assert.Contains(cleanupError, errors);
        Assert.Single(compensation.Deletes);
    }
}
