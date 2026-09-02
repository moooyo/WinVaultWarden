using System.Security.Cryptography;
using System.Text;
using System.Net;
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
    private static readonly DateTimeOffset DefaultRevision =
        new(2026, 6, 24, 1, 2, 3, 456, TimeSpan.Zero);

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

    // Applies the post-sync vault to the real session so the service reads committed attachments.
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

    private VaultSession NewUnlockedSession(params Cipher[] ciphers)
    {
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(_userKey);
        if (ciphers.Length > 0)
            session.SetSnapshot(SnapshotWith(ciphers));
        return session;
    }

    private static DecryptedVault SnapshotWith(params Cipher[] ciphers)
        => new(
            new AccountInfo("test-account", "me@example.com", new ServerOrigin("https://vault.example"), "M", string.Empty),
            Array.Empty<Folder>(),
            ciphers,
            SkippedCipherCount: 0,
            SkippedFolderCount: 0);

    private static Cipher CipherWithAttachments(string id, params CipherAttachment[] attachments)
        => new()
        {
            Id = id,
            Type = CipherType.Login,
            Name = "Item",
            RevisionDate = DefaultRevision,
            Attachments = attachments,
        };

    // Builds a modern attachment whose independent key is wrapped by the item key.
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
        var session = NewUnlockedSession(CipherWithAttachments("c-1"));
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
        // Metadata contains only encrypted values and the exact encrypted payload length.
        var req = api.LastCreateRequest!;
        Assert.StartsWith("2.", req.Key);
        Assert.StartsWith("2.", req.FileName);
        Assert.NotEqual("report.pdf", req.FileName);
        Assert.Equal(api.LastUploadBuffer!.LongLength, req.FileSize);
        Assert.Equal(DefaultRevision.UtcDateTime, req.LastKnownRevisionDate);
        // The payload uses the returned URL and EncArrayBuffer version 2.
        Assert.Equal(FileUploadType.Direct, api.LastUploadType);
        Assert.Equal("/ciphers/c-1/attachment/att-1", api.LastUploadUrl);
        Assert.Equal("c-1", api.LastUploadCipherId);
        Assert.Equal("att-1", api.LastUploadAttachmentId);
        Assert.Equal(req.FileName, api.LastUploadFileName);
        Assert.Equal((byte)2, api.LastUploadBuffer![0]);
        // The result comes from the post-upload synchronized session snapshot.
        var att = Assert.Single(result);
        Assert.Equal("att-1", att.Id);
        Assert.Equal("report.pdf", att.FileName);
    }

    [Fact]
    public async Task Upload_WithCipherKey_WrapsUnderItemKeyNotUserKey()
    {
        var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var cipherKeyEnc = _crypto.Encrypt(itemKey.FullKey, _userKey).ToString();
        var session = NewUnlockedSession(new Cipher
        {
            Id = "c-2",
            Type = CipherType.Login,
            Key = cipherKeyEnc,
            Name = "Item",
            RevisionDate = DefaultRevision,
        });
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("c-2", cipherKeyEnc),
        };
        var snapshot = SnapshotWith(CipherWithAttachments("c-2"));
        var service = CreateService(FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api), session, new FakeSync(session, snapshot));

        await service.UploadAsync("c-2", Source("f.bin", [1, 2, 3]), TestContext.Current.CancellationToken);

        // Only the item key can unwrap the independent attachment key.
        var wrapped = api.LastCreateRequest!.Key;
        var attKey = _attCrypto.UnwrapKey(wrapped, itemKey);
        Assert.Equal(64, attKey.FullKey.Length);
        Assert.Throws<CryptographicException>(() => _attCrypto.UnwrapKey(wrapped, _userKey));
    }

    [Fact]
    public async Task Upload_SnapshotReplacementBeforeSlot_UsesLatestCompatibleRevision()
    {
        var opening = CipherWithAttachments("c-replaced");
        var replacementRevision = DefaultRevision.AddMinutes(5);
        var replacement = new Cipher
        {
            Id = opening.Id,
            Type = opening.Type,
            Name = "Updated locally",
            Key = opening.Key,
            RevisionDate = replacementRevision,
        };
        var session = NewUnlockedSession(opening);
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto(opening.Id, cipherKeyEnc: null),
            AfterGetCipher = _ => session.SetSnapshot(SnapshotWith(replacement)),
        };
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, SnapshotWith(replacement)));

        await service.UploadAsync(
            opening.Id,
            Source("fresh.bin", [1, 2, 3]),
            TestContext.Current.CancellationToken);

        Assert.Equal(replacementRevision.UtcDateTime, api.LastCreateRequest!.LastKnownRevisionDate);
        Assert.Equal(["get-cipher", "create-v2", "upload"], api.Calls);
    }

    [Fact]
    public async Task Upload_EncryptionKeyReplacementBeforeSlot_ThrowsRecoverableConflictWithoutCreate()
    {
        var opening = CipherWithAttachments("c-key-changed");
        var session = NewUnlockedSession(opening);
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto(opening.Id, cipherKeyEnc: null),
            AfterGetCipher = _ => session.SetSnapshot(SnapshotWith(new Cipher
            {
                Id = opening.Id,
                Type = CipherType.Login,
                Name = "Rekeyed",
                Key = "2.changed-item-key",
                RevisionDate = DefaultRevision.AddMinutes(1),
            })),
        };
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null),
            compensation);

        var error = await Assert.ThrowsAsync<AttachmentRevisionConflictException>(() =>
            service.UploadAsync(
                opening.Id,
                Source("stale.bin", [1, 2, 3]),
                TestContext.Current.CancellationToken));

        Assert.True(error.CanRetryAfterSync);
        Assert.Equal(opening.Id, error.CipherId);
        Assert.Equal(["get-cipher"], api.Calls);
        Assert.Null(api.LastCreateRequest);
        Assert.Null(api.LastUploadBuffer);
        Assert.Empty(compensation.Deletes);
    }

    [Fact]
    public async Task Upload_LeaseInvalidatedAfterCipherRead_DoesNotCreateSlot()
    {
        var cipher = CipherWithAttachments("c-expired-before-slot");
        var session = NewUnlockedSession(cipher);
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto(cipher.Id, cipherKeyEnc: null),
            AfterGetCipher = _ => session.Lock(),
        };
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.UploadAsync(
                cipher.Id,
                Source("expired.bin", [1]),
                TestContext.Current.CancellationToken));

        Assert.Equal(["get-cipher"], api.Calls);
        Assert.Null(api.LastCreateRequest);
        Assert.Null(api.LastUploadBuffer);
    }

    [Fact]
    public async Task Upload_ForeignAccountSnapshot_IsRejectedBeforeNetworkAccess()
    {
        var cipher = CipherWithAttachments("c-account-bound");
        var session = NewUnlockedSession(cipher);
        session.SetSnapshot(new DecryptedVault(
            new AccountInfo(
                "other-account",
                "other@example.com",
                new ServerOrigin("https://vault.example"),
                "O",
                string.Empty),
            [],
            [cipher],
            0,
            0));
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto(cipher.Id, cipherKeyEnc: null),
        };
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null));

        var error = await Assert.ThrowsAsync<AttachmentRevisionConflictException>(() =>
            service.UploadAsync(
                cipher.Id,
                Source("foreign.bin", [1]),
                TestContext.Current.CancellationToken));

        Assert.Equal(cipher.Id, error.CipherId);
        Assert.True(error.CanRetryAfterSync);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Upload_SlotRevisionConflict_DoesNotUploadBytes(
        HttpStatusCode statusCode)
    {
        var cipher = CipherWithAttachments("c-server-conflict");
        var session = NewUnlockedSession(cipher);
        var writeError = new VaultWriteException(
            statusCode,
            "The item cannot be saved because it is out of date.",
            validationErrors: null);
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto(cipher.Id, cipherKeyEnc: null),
            CreateThrows = writeError,
        };
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null),
            compensation);

        var error = await Assert.ThrowsAsync<AttachmentRevisionConflictException>(() =>
            service.UploadAsync(
                cipher.Id,
                Source("conflict.bin", [1, 2]),
                TestContext.Current.CancellationToken));

        Assert.Same(writeError, error.InnerException);
        Assert.Equal(DefaultRevision, error.LastKnownRevisionDate);
        Assert.True(error.CanRetryAfterSync);
        Assert.Equal(["get-cipher", "create-v2"], api.Calls);
        Assert.Null(api.LastUploadBuffer);
        Assert.Empty(compensation.Deletes);
    }

    [Fact]
    public async Task Upload_NonRevisionBadRequestPreservesServerErrorAndDoesNotUploadBytes()
    {
        var cipher = CipherWithAttachments("c-server-rejection");
        var session = NewUnlockedSession(cipher);
        var writeError = new VaultWriteException(
            HttpStatusCode.BadRequest,
            "Not enough storage available for this organization.",
            validationErrors: null);
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto(cipher.Id, cipherKeyEnc: null),
            CreateThrows = writeError,
        };
        var compensation = new FakeCompensationClient();
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null),
            compensation);

        var error = await Assert.ThrowsAsync<VaultWriteException>(() =>
            service.UploadAsync(
                cipher.Id,
                Source("rejected.bin", [1, 2]),
                TestContext.Current.CancellationToken));

        Assert.Same(writeError, error);
        Assert.Equal(["get-cipher", "create-v2"], api.Calls);
        Assert.Null(api.LastUploadBuffer);
        Assert.Empty(compensation.Deletes);
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
        // Oversized files are rejected before encryption or network access.
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
        // Legacy payloads are encrypted directly with the item key.
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
        var session = new VaultSession();
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
    public async Task OrganizationCipherOperations_AreRejectedBeforeMutation()
    {
        var session = NewUnlockedSession();
        session.SetSnapshot(SnapshotWith(new Cipher
        {
            Id = "org-item",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            Name = "Shared",
            RevisionDate = DefaultRevision,
            CanEdit = false,
            CanViewPassword = false,
            Login = new CipherLogin(null, null, null, []),
        }));
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = BuildCipherDto("org-item", null) with { OrganizationId = "org-1" },
        };
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, null));

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            service.UploadAsync(
                "org-item",
                Source("file.bin", [1]),
                TestContext.Current.CancellationToken));
        await using var destination = new MemoryStream();
        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            service.DownloadAsync(
                "org-item",
                "attachment",
                destination,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            service.DeleteAsync(
                "org-item",
                "attachment",
                TestContext.Current.CancellationToken));

        Assert.Equal(["get-cipher", "get-cipher"], api.Calls);
    }

    [Fact]
    public async Task OrganizationCipherOperations_UseOrganizationAndItemKeysWhenPermitted()
    {
        using var rsa = RSA.Create(2048);
        using var organizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        using var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var privateKeyBytes = rsa.ExportPkcs8PrivateKey();
        var encryptedPrivateKey = _crypto.Encrypt(privateKeyBytes, _userKey).ToString();
        CryptographicOperations.ZeroMemory(privateKeyBytes);
        var encryptedOrganizationKey = _crypto.EncryptRsa(
            organizationKey.FullKey,
            rsa.ExportSubjectPublicKeyInfo()).ToString();
        var encryptedItemKey = _crypto.Encrypt(itemKey.FullKey, organizationKey).ToString();
        var plaintext = Encoding.UTF8.GetBytes("organization attachment");
        var dto = CipherDtoWithNewFormatAttachment(
            "org-editable",
            "att-existing",
            "existing.txt",
            plaintext,
            "https://files.example/org-attachment",
            itemKey,
            encryptedItemKey,
            out var encryptedDownload) with
        {
            OrganizationId = "org-1",
            Edit = true,
            ViewPassword = true,
        };
        var cipher = new Cipher
        {
            Id = "org-editable",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1"],
            Key = encryptedItemKey,
            Name = "Shared",
            RevisionDate = DefaultRevision,
            CanEdit = true,
            CanViewPassword = true,
            CanDelete = true,
            CanRestore = true,
            Login = new CipherLogin(null, null, null, []),
        };
        var snapshot = SnapshotWith(cipher) with
        {
            Organizations =
            [
                new VaultOrganization("org-1", "Team", 2, 2, true)
                {
                    EncryptedKey = encryptedOrganizationKey,
                },
            ],
            Collections = [new VaultCollection("collection-1", "org-1", "Shared", false, false, false, null)],
        };
        var session = NewUnlockedSession();
        session.SetEncryptedPrivateKey(encryptedPrivateKey);
        session.SetSnapshot(snapshot);
        var api = new FakeAttachmentApiClient
        {
            GetCipherResult = dto,
            DownloadBytes = encryptedDownload,
        };
        var service = CreateService(
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api),
            session,
            new FakeSync(session, snapshot));

        await service.UploadAsync(
            cipher.Id,
            Source("new.txt", Encoding.UTF8.GetBytes("new attachment")),
            TestContext.Current.CancellationToken);
        using var uploadedAttachmentKey = _attCrypto.UnwrapKey(api.LastCreateRequest!.Key, itemKey);

        await using var destination = new MemoryStream();
        await service.DownloadAsync(
            cipher.Id,
            "att-existing",
            destination,
            TestContext.Current.CancellationToken);
        await service.DeleteAsync(
            cipher.Id,
            "att-existing",
            TestContext.Current.CancellationToken);

        Assert.Equal(64, uploadedAttachmentKey.FullKey.Length);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Equal(
            ["get-cipher", "create-v2", "upload", "get-cipher", "download", "delete"],
            api.Calls);
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
        var session = NewUnlockedSession(CipherWithAttachments("c-1"));
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

        // The original second-step upload error remains observable.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync("c-1", Source("report.pdf", Encoding.UTF8.GetBytes("data")), TestContext.Current.CancellationToken));
        Assert.Same(boom, ex);

        // Cleanup targets the attachment ID created by the first step.
        Assert.Equal(new[] { "get-cipher", "create-v2", "upload" }, api.Calls);
        var deleted = Assert.Single(compensation.Deletes);
        Assert.Same(api.CreateAuthorization, deleted.Binding);
        Assert.Equal("c-1", deleted.CipherId);
        Assert.Equal("att-orphan", deleted.AttachmentId);
        // Failed payload transfer must not trigger a sync.
        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task Upload_WhenCancelledDuringDataUpload_StillUsesIndependentCompensationToken()
    {
        var session = NewUnlockedSession(CipherWithAttachments("c-2"));
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
        var session = NewUnlockedSession(CipherWithAttachments("c-unknown"));
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
        var session = NewUnlockedSession(CipherWithAttachments("c-lease"));
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
        var session = NewUnlockedSession(CipherWithAttachments("c-committed"));
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
        var session = NewUnlockedSession(CipherWithAttachments("c-3"));
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
