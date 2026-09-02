using System.Security.Cryptography;
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

public sealed class VaultOrganizationPlacementTests
{
    private sealed class PlacementWriteApi : IVaultWriteApiClient
    {
        public ShareCipherRequest? Shared { get; private set; }
        public BulkShareCiphersRequest? BulkShared { get; private set; }
        public CipherCollectionsRequest? Collections { get; private set; }
        public BulkCipherCollectionsRequest? BulkCollections { get; private set; }
        public Exception? Failure { get; set; }

        public Task ShareExistingCipherAsync(string cipherId, ShareCipherRequest request, CancellationToken ct = default)
        {
            Shared = request;
            return Result();
        }

        public Task BulkShareCiphersAsync(BulkShareCiphersRequest request, CancellationToken ct = default)
        {
            BulkShared = request;
            return Result();
        }

        public Task SetCipherCollectionsAsync(string cipherId, CipherCollectionsRequest request, CancellationToken ct = default)
        {
            Collections = request;
            return Result();
        }

        public Task BulkUpdateCipherCollectionsAsync(BulkCipherCollectionsRequest request, CancellationToken ct = default)
        {
            BulkCollections = request;
            return Result();
        }

        private Task Result() => Failure is null ? Task.CompletedTask : Task.FromException(Failure);

        public Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ApplyingSync(VaultSession session) : ISyncService
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Func<IReadOnlyList<Cipher>>? SnapshotFactory { get; set; }

        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
            SyncAsync(session.CaptureLease(), ct);

        public Task<IReadOnlyList<Cipher>> SyncAsync(VaultSessionLease lease, CancellationToken ct = default)
        {
            Calls++;
            if (Failure is not null)
                return Task.FromException<IReadOnlyList<Cipher>>(Failure);
            var ciphers = SnapshotFactory?.Invoke() ?? session.Ciphers;
            if (!session.TryCommit(lease, () => session.SetSnapshot(new DecryptedVault(
                session.Account,
                session.Folders,
                ciphers,
                session.SkippedCipherCount,
                session.SkippedFolderCount)
            {
                Organizations = session.Organizations,
                Collections = session.Collections,
                Policies = session.Policies,
            })))
            {
                return Task.FromException<IReadOnlyList<Cipher>>(
                    new OperationCanceledException(lease.CancellationToken));
            }
            return Task.FromResult(ciphers);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(params Cipher[] ciphers)
        {
            Crypto = new CryptoService();
            Session = new VaultSession();
            Session.BeginAccountSession("account", "user@example.com", new ServerOrigin("https://vault.example"));
            var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
            OrganizationKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
            using var rsa = RSA.Create(2048);
            var privateKey = rsa.ExportPkcs8PrivateKey();
            try
            {
                Session.SetUnlockedKey(userKey);
                Session.SetEncryptedPrivateKey(Crypto.Encrypt(privateKey, userKey).ToString());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
            var encryptedOrganizationKey = Crypto.EncryptRsa(
                OrganizationKey.FullKey,
                rsa.ExportSubjectPublicKeyInfo()).ToString();
            Session.SetSnapshot(new DecryptedVault(Session.Account, [], ciphers, 0, 0)
            {
                Organizations =
                [
                    new VaultOrganization("org-1", "Team", 2, 2, true)
                    {
                        EncryptedKey = encryptedOrganizationKey,
                    },
                ],
                Collections =
                [
                    new VaultCollection("writable-1", "org-1", "One", false, false, false, null),
                    new VaultCollection("writable-2", "org-1", "Two", false, false, false, null),
                    new VaultCollection("readonly", "org-1", "Read only", true, false, false, null),
                    new VaultCollection("managed", "org-1", "Managed", true, true, true, null),
                ],
            });
            Api = new PlacementWriteApi();
            Sync = new ApplyingSync(Session);
            Service = new VaultWriteService(
                FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(Api),
                new CipherEncryptor(Crypto),
                Sync,
                Session,
                new OrganizationKeyResolver(Crypto));
        }

        public CryptoService Crypto { get; }
        public VaultSession Session { get; }
        public SymmetricCryptoKey OrganizationKey { get; }
        public PlacementWriteApi Api { get; }
        public ApplyingSync Sync { get; }
        public VaultWriteService Service { get; }

        public void Dispose()
        {
            Session.Lock();
            OrganizationKey.Dispose();
        }
    }

    private static Cipher PersonalCipher(CryptoService crypto, SymmetricCryptoKey userKey, string id = "cipher-1")
    {
        using var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        return new Cipher
        {
            Id = id,
            Type = CipherType.Login,
            Key = crypto.Encrypt(itemKey.FullKey, userKey).ToString(),
            Name = "Personal",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        };
    }

    private static Cipher WithPlacement(Cipher cipher, string? organizationId, params string[] collectionIds) => new()
    {
        Id = cipher.Id,
        Type = cipher.Type,
        OrganizationId = organizationId,
        CollectionIds = collectionIds,
        Key = cipher.Key,
        FolderId = cipher.FolderId,
        Favorite = cipher.Favorite,
        Reprompt = cipher.Reprompt,
        Name = cipher.Name,
        Notes = cipher.Notes,
        CreationDate = cipher.CreationDate,
        RevisionDate = cipher.RevisionDate,
        CanEdit = cipher.CanEdit,
        CanViewPassword = cipher.CanViewPassword,
        CanDelete = cipher.CanDelete,
        CanRestore = cipher.CanRestore,
        Login = cipher.Login,
        Fields = cipher.Fields,
        Attachments = cipher.Attachments,
        PasswordHistory = cipher.PasswordHistory,
    };

    [Fact]
    public async Task TransferCipher_RewrapsItemKeyAndCommitsExactCollections()
    {
        using var seedUserKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var crypto = new CryptoService();
        var cipher = PersonalCipher(crypto, seedUserKey);
        using var fixture = new Fixture(cipher);
        fixture.Sync.SnapshotFactory = () => [WithPlacement(cipher, "org-1", "writable-1", "writable-2")];

        await fixture.Service.TransferCipherToOrganizationAsync(
            CipherPlacementBaseline.Capture(cipher),
            "org-1",
            ["writable-1", "writable-2"],
            TestContext.Current.CancellationToken);

        Assert.Equal(["writable-1", "writable-2"], fixture.Api.Shared!.CollectionIds);
        Assert.Equal(cipher.RevisionDate.ToUniversalTime().ToString("o"), fixture.Api.Shared.Cipher.LastKnownRevisionDate);
        using var originalItemKey = crypto.DecryptItemKey(cipher.Key!, seedUserKey);
        using var transferredItemKey = crypto.DecryptItemKey(fixture.Api.Shared.Cipher.Key!, fixture.OrganizationKey);
        Assert.Equal(originalItemKey.FullKey, transferredItemKey.FullKey);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Fact]
    public async Task TransferCipher_ProviderOnlyOrganizationIsRejectedBeforeApi()
    {
        using var seedUserKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(index => (byte)index).ToArray());
        var cipher = PersonalCipher(new CryptoService(), seedUserKey);
        using var fixture = new Fixture(cipher);
        var organization = Assert.Single(fixture.Session.Organizations);
        fixture.Session.SetSnapshot(new DecryptedVault(
            fixture.Session.Account,
            fixture.Session.Folders,
            fixture.Session.Ciphers,
            0,
            0)
        {
            Organizations =
            [
                organization with
                {
                    IsMember = false,
                    IsProviderUser = true,
                    IsProviderOrganization = true,
                },
            ],
            Collections = fixture.Session.Collections,
        });

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            fixture.Service.TransferCipherToOrganizationAsync(
                CipherPlacementBaseline.Capture(cipher),
                "org-1",
                ["writable-1"],
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.Shared);
        Assert.Equal(0, fixture.Sync.Calls);
    }

    [Fact]
    public async Task TransferCipher_LegacyAttachment_IsRejectedBeforeApi()
    {
        var cipher = new Cipher
        {
            Id = "legacy",
            Type = CipherType.Login,
            Name = "Legacy",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
            Attachments = [new CipherAttachment("attachment", "file.txt", 1, "1 B")],
        };
        using var fixture = new Fixture(cipher);

        await Assert.ThrowsAsync<LegacyCipherAttachmentTransferException>(() =>
            fixture.Service.TransferCipherToOrganizationAsync(
                CipherPlacementBaseline.Capture(cipher),
                "org-1",
                ["writable-1"],
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.Shared);
        Assert.Equal(0, fixture.Sync.Calls);
    }

    [Fact]
    public async Task SetCollections_PreservesReadOnlyAssignmentAndAddsWritableCollection()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "readonly", "writable-1");
        using var fixture = new Fixture(cipher);
        fixture.Sync.SnapshotFactory = () => [WithPlacement(cipher, "org-1", "readonly", "writable-1", "writable-2")];

        await fixture.Service.SetCipherCollectionsAsync(
            CipherPlacementBaseline.Capture(cipher),
            ["readonly", "writable-1", "writable-2"],
            TestContext.Current.CancellationToken);

        Assert.Equal(["writable-1", "writable-2"], fixture.Api.Collections!.CollectionIds);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Fact]
    public async Task SetCollections_ReadOnlyOnlyTarget_SendsEmptyWritableBody()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "readonly", "writable-1");
        using var fixture = new Fixture(cipher);
        fixture.Sync.SnapshotFactory = () => [WithPlacement(cipher, "org-1", "readonly")];

        await fixture.Service.SetCipherCollectionsAsync(
            CipherPlacementBaseline.Capture(cipher),
            ["readonly"],
            TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Api.Collections!.CollectionIds);
        Assert.Equal(["readonly"], Assert.Single(fixture.Session.Ciphers).CollectionIds);
    }

    [Fact]
    public async Task SetCollections_EmptyTarget_SendsEmptyWritableBody()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "writable-1");
        using var fixture = new Fixture(cipher);
        fixture.Sync.SnapshotFactory = () => [WithPlacement(cipher, "org-1")];

        await fixture.Service.SetCipherCollectionsAsync(
            CipherPlacementBaseline.Capture(cipher),
            [],
            TestContext.Current.CancellationToken);

        Assert.Empty(fixture.Api.Collections!.CollectionIds);
        Assert.Empty(Assert.Single(fixture.Session.Ciphers).CollectionIds);
    }

    [Fact]
    public async Task SetCollections_RemovingReadOnlyAssignment_IsRejectedBeforeApi()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "readonly", "writable-1");
        using var fixture = new Fixture(cipher);

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            fixture.Service.SetCipherCollectionsAsync(
                CipherPlacementBaseline.Capture(cipher),
                ["writable-1"],
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.Collections);
    }

    [Fact]
    public async Task SetCollections_AddingReadOnlyAssignment_IsRejectedBeforeApi()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "writable-1");
        using var fixture = new Fixture(cipher);

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            fixture.Service.SetCipherCollectionsAsync(
                CipherPlacementBaseline.Capture(cipher),
                ["writable-1", "readonly"],
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.Collections);
    }

    [Fact]
    public async Task SetCollections_ServerDropsReadOnlyAssignment_FailsFullTargetPostcondition()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "readonly", "writable-1");
        using var fixture = new Fixture(cipher);
        fixture.Sync.SnapshotFactory = () =>
            [WithPlacement(cipher, "org-1", "writable-1", "writable-2")];

        var error = await Assert.ThrowsAsync<VaultMutationReconciliationException>(() =>
            fixture.Service.SetCipherCollectionsAsync(
                CipherPlacementBaseline.Capture(cipher),
                ["readonly", "writable-1", "writable-2"],
                TestContext.Current.CancellationToken));

        Assert.Equal(["writable-1", "writable-2"], fixture.Api.Collections!.CollectionIds);
        Assert.Equal(["org-cipher"], error.UnresolvedCipherIds);
    }

    [Fact]
    public async Task SetCollections_ApiFailureAfterPartialCommit_ReportsReconciliation()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "readonly", "writable-1");
        using var fixture = new Fixture(cipher);
        fixture.Api.Failure = new HttpRequestException("response lost after partial commit");
        fixture.Sync.SnapshotFactory = () =>
            [WithPlacement(cipher, "org-1", "writable-1", "writable-2")];

        var error = await Assert.ThrowsAsync<VaultMutationReconciliationException>(() =>
            fixture.Service.SetCipherCollectionsAsync(
                CipherPlacementBaseline.Capture(cipher),
                ["readonly", "writable-1", "writable-2"],
                TestContext.Current.CancellationToken));

        Assert.Equal(["org-cipher"], error.UnresolvedCipherIds);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Fact]
    public async Task SetCollections_ApiFailureWithoutCommit_RethrowsApiError()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "org-cipher",
            Type = CipherType.Login,
            Name = "Organization",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "readonly", "writable-1");
        using var fixture = new Fixture(cipher);
        var failure = new HttpRequestException("request rejected");
        fixture.Api.Failure = failure;

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Service.SetCipherCollectionsAsync(
                CipherPlacementBaseline.Capture(cipher),
                ["readonly", "writable-1", "writable-2"],
                TestContext.Current.CancellationToken));

        Assert.Same(failure, error);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Fact]
    public async Task Transfer_ApiAndRecoverySyncFailure_MarksSnapshotStale()
    {
        using var seedUserKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var cipher = PersonalCipher(new CryptoService(), seedUserKey);
        using var fixture = new Fixture(cipher);
        fixture.Api.Failure = new HttpRequestException("response lost");
        fixture.Sync.Failure = new TimeoutException("sync failed");

        await Assert.ThrowsAsync<VaultBatchMutationRecoveryException>(() =>
            fixture.Service.TransferCipherToOrganizationAsync(
                CipherPlacementBaseline.Capture(cipher),
                "org-1",
                ["writable-1"],
                TestContext.Current.CancellationToken));

        Assert.True(fixture.Session.DataStale);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Fact]
    public async Task Transfer_ApiFailureButAuthoritativeSyncProvesCommit_ReturnsSuccess()
    {
        using var seedUserKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var cipher = PersonalCipher(new CryptoService(), seedUserKey);
        using var fixture = new Fixture(cipher);
        var failure = new HttpRequestException("response lost");
        fixture.Api.Failure = failure;
        fixture.Sync.SnapshotFactory = () => [WithPlacement(cipher, "org-1", "writable-1")];

        await fixture.Service.TransferCipherToOrganizationAsync(
            CipherPlacementBaseline.Capture(cipher),
            "org-1",
            ["writable-1"],
            TestContext.Current.CancellationToken);

        Assert.Equal("org-1", Assert.Single(fixture.Session.Ciphers).OrganizationId);
        Assert.False(fixture.Session.DataStale);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Fact]
    public async Task BulkShare_UsesPerCipherIdsAndVerifiesPostcondition()
    {
        using var seedUserKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var crypto = new CryptoService();
        var first = PersonalCipher(crypto, seedUserKey, "first");
        var second = PersonalCipher(crypto, seedUserKey, "second");
        using var fixture = new Fixture(first, second);
        fixture.Sync.SnapshotFactory = () =>
        [
            WithPlacement(first, "org-1", "writable-1"),
            WithPlacement(second, "org-1", "writable-1"),
        ];

        await fixture.Service.TransferCiphersToOrganizationAsync(
            [CipherPlacementBaseline.Capture(first), CipherPlacementBaseline.Capture(second)],
            "org-1",
            ["writable-1"],
            TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], fixture.Api.BulkShared!.Ciphers.Select(cipher => cipher.Id));
    }

    [Fact]
    public async Task BulkCollections_SilentServerSkip_IsReportedAfterSync()
    {
        var first = WithPlacement(new Cipher
        {
            Id = "first", Type = CipherType.Login, Name = "First",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "writable-1");
        var second = WithPlacement(new Cipher
        {
            Id = "second", Type = CipherType.Login, Name = "Second",
            RevisionDate = first.RevisionDate,
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "writable-1");
        using var fixture = new Fixture(first, second);
        fixture.Sync.SnapshotFactory = () =>
        [
            WithPlacement(first, "org-1", "writable-1", "writable-2"),
            second,
        ];

        var error = await Assert.ThrowsAsync<VaultMutationReconciliationException>(() =>
            fixture.Service.BulkUpdateCipherCollectionsAsync(
                [CipherPlacementBaseline.Capture(first), CipherPlacementBaseline.Capture(second)],
                "org-1",
                ["writable-2"],
                removeCollections: false,
                TestContext.Current.CancellationToken));

        Assert.Equal(["second"], error.UnresolvedCipherIds);
        Assert.Equal(["first", "second"], fixture.Api.BulkCollections!.CipherIds);
        Assert.False(fixture.Session.DataStale);
    }

    [Fact]
    public async Task BulkCollections_CollectionManagerCanUpdateReadOnlyCipher()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "managed-cipher",
            Type = CipherType.Login,
            Name = "Managed",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            CanEdit = false,
            CanViewPassword = false,
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "managed");
        using var fixture = new Fixture(cipher);
        fixture.Sync.SnapshotFactory = () =>
            [WithPlacement(cipher, "org-1", "managed", "writable-2")];

        await fixture.Service.BulkUpdateCipherCollectionsAsync(
            [CipherPlacementBaseline.Capture(cipher)],
            "org-1",
            ["writable-2"],
            removeCollections: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(["writable-2"], fixture.Api.BulkCollections!.CollectionIds);
    }

    [Fact]
    public async Task BulkCollections_HiddenPasswordWithoutManage_IsRejectedBeforeApi()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "hidden-cipher",
            Type = CipherType.Login,
            Name = "Hidden",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            CanEdit = true,
            CanViewPassword = false,
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "writable-1");
        using var fixture = new Fixture(cipher);

        await Assert.ThrowsAsync<OrganizationCipherOperationException>(() =>
            fixture.Service.BulkUpdateCipherCollectionsAsync(
                [CipherPlacementBaseline.Capture(cipher)],
                "org-1",
                ["writable-2"],
                removeCollections: false,
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.BulkCollections);
    }

    [Fact]
    public async Task BulkCollections_UnsupportedCipherType_IsRejectedBeforeApi()
    {
        var cipher = WithPlacement(new Cipher
        {
            Id = "bank",
            Type = CipherType.BankAccount,
            Name = "Bank",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            BankAccount = new CipherBankAccount(
                "Bank", "Name", "Checking", "1", "2", "3", "4", "5", "6", "7"),
        }, "org-1", "writable-1");
        using var fixture = new Fixture(cipher);

        await Assert.ThrowsAsync<UnsupportedCipherTypeWriteException>(() =>
            fixture.Service.BulkUpdateCipherCollectionsAsync(
                [CipherPlacementBaseline.Capture(cipher)],
                "org-1",
                ["writable-2"],
                removeCollections: false,
                TestContext.Current.CancellationToken));

        Assert.Null(fixture.Api.BulkCollections);
    }

    [Fact]
    public async Task BulkCollections_ApiFailureAfterPartialCommit_ReportsUnresolvedCiphers()
    {
        var first = WithPlacement(new Cipher
        {
            Id = "first", Type = CipherType.Login, Name = "First",
            RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "writable-1");
        var second = WithPlacement(new Cipher
        {
            Id = "second", Type = CipherType.Login, Name = "Second",
            RevisionDate = first.RevisionDate,
            Login = new CipherLogin("user", "secret", null, []),
        }, "org-1", "writable-1");
        using var fixture = new Fixture(first, second);
        fixture.Api.Failure = new HttpRequestException("response lost after first commit");
        fixture.Sync.SnapshotFactory = () =>
        [
            WithPlacement(first, "org-1", "writable-1", "writable-2"),
            second,
        ];

        var error = await Assert.ThrowsAsync<VaultMutationReconciliationException>(() =>
            fixture.Service.BulkUpdateCipherCollectionsAsync(
                [CipherPlacementBaseline.Capture(first), CipherPlacementBaseline.Capture(second)],
                "org-1",
                ["writable-2"],
                removeCollections: false,
                TestContext.Current.CancellationToken));

        Assert.Equal(["second"], error.UnresolvedCipherIds);
        Assert.False(fixture.Session.DataStale);
        Assert.Equal(1, fixture.Sync.Calls);
    }
}
