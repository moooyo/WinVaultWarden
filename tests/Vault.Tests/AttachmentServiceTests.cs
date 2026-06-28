using System.Security.Cryptography;
using System.Text;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
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

    // 把 sync 后的整库快照塞进真实 VaultSession,使 service 能从 session.Ciphers 读回附件。
    private sealed class FakeSync : ISyncService
    {
        private readonly VaultSession _session;
        private readonly DecryptedVault? _snapshot;
        public int Calls { get; private set; }

        public FakeSync(VaultSession session, DecryptedVault? snapshot)
        {
            _session = session;
            _snapshot = snapshot;
        }

        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            if (_snapshot is not null)
                _session.SetSnapshot(_snapshot);
            return Task.FromResult(_snapshot?.Ciphers ?? (IReadOnlyList<Cipher>)Array.Empty<Cipher>());
        }
    }

    private VaultSession NewUnlockedSession()
    {
        var session = new VaultSession();
        session.SetUnlockedKey(_userKey);
        return session;
    }

    private static DecryptedVault SnapshotWith(Cipher cipher)
        => new(
            new AccountInfo("me@example.com", "https://vault.example", "M", string.Empty),
            Array.Empty<Folder>(),
            new[] { cipher },
            0);

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
                AttachmentId: "att-1", Url: "/ciphers/c-1/attachment/att-1", FileUploadType: 0),
        };
        var snapshot = SnapshotWith(CipherWithAttachments("c-1",
            new CipherAttachment("att-1", "report.pdf", 12345, "12 KB")));
        var sync = new FakeSync(session, snapshot);
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, sync);
        var plaintext = Encoding.UTF8.GetBytes("hello attachment");

        var result = await service.UploadAsync("c-1", "report.pdf", plaintext, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "get-cipher", "create-v2", "upload" }, api.Calls);
        Assert.Equal(1, sync.Calls);
        // create 请求:key 是 type-2 EncString;fileName 是加密后的(非明文);fileSize == buffer 长度。
        var req = api.LastCreateRequest!;
        Assert.StartsWith("2.", req.Key);
        Assert.StartsWith("2.", req.FileName);
        Assert.NotEqual("report.pdf", req.FileName);
        Assert.Equal(api.LastUploadBuffer!.LongLength, req.FileSize);
        // upload 到 create 返回的 Url,文件名是加密后的 fileName,缓冲首字节为 EncArrayBuffer 版本 2。
        Assert.Equal("/ciphers/c-1/attachment/att-1", api.LastUploadUrl);
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
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, new FakeSync(session, snapshot));

        await service.UploadAsync("c-2", "f.bin", new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

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
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, new FakeSync(session, null));
        // 比上限大 1 字节;不实际分配 100MiB+,用稀疏数组也会真实占内存,这里直接构造刚好超限。
        var tooBig = new byte[AttachmentService.MaxPlaintextBytes + 1];

        var ex = await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
            service.UploadAsync("c-3", "big.bin", tooBig, TestContext.Current.CancellationToken));

        Assert.Equal(tooBig.LongLength, ex.ActualBytes);
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
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, new FakeSync(session, null));

        var result = await service.DownloadAsync("c-4", "att-4", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "get-cipher", "download" }, api.Calls);
        Assert.Equal("https://files.example/att-4", api.LastDownloadUrl);
        Assert.Equal(plaintext, result);
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
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, new FakeSync(session, null));

        var result = await service.DownloadAsync("c-5", "att-legacy", TestContext.Current.CancellationToken);

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public async Task Download_AttachmentNotFound_Throws()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient { GetCipherResult = BuildCipherDto("c-6", null) };
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, new FakeSync(session, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAsync("c-6", "missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Delete_CallsDeleteAndResyncs_ReturnsRefreshedList()
    {
        var session = NewUnlockedSession();
        var api = new FakeAttachmentApiClient();
        var snapshot = SnapshotWith(CipherWithAttachments("c-7",
            new CipherAttachment("att-keep", "keep.txt", 7, "7 B")));
        var sync = new FakeSync(session, snapshot);
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, sync);

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
        var service = new AttachmentService(api, _attCrypto, _decryptor, session, new FakeSync(session, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync("c-8", "f", new byte[] { 1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAsync("c-8", "a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync("c-8", "a", TestContext.Current.CancellationToken));
    }
}
