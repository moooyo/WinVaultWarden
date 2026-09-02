using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using App.Services;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class DpapiTokenStoreV2Tests
{
    [Fact]
    public void AccountBindingUsesEveryEndpointAndDomainSeparatesEntropy()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/base");
        var customApi = new ServerEndpoints(
            endpoints.WebVault,
            new ServerEndpoint("https://api.example/custom"),
            endpoints.Identity,
            endpoints.Notifications,
            endpoints.Icons,
            endpoints.SendWeb);
        var first = new PersistedAccountBinding("account-a", endpoints);
        var second = new PersistedAccountBinding("account-a", customApi);
        var revision = Guid.NewGuid();
        var firstEntropy = first.CreateBlobProtectionEntropy(revision);
        var secondEntropy = second.CreateBlobProtectionEntropy(revision);
        try
        {
            Assert.NotEqual(first.CreateIdentifier(), second.CreateIdentifier());
            Assert.False(firstEntropy.SequenceEqual(secondEntropy));
            Assert.NotEqual(first.CreateIdentifier(), Convert.ToHexStringLower(firstEntropy));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(firstEntropy);
            CryptographicOperations.ZeroMemory(secondEntropy);
        }
    }

    [Fact]
    public void MetadataListingDecryptsOnlyIndexAndContainsNoSessionSecrets()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var first = Session("account-a", "https://a.example", "refresh-secret-a");
        var second = Session("account-b", "https://b.example", "refresh-secret-b");

        store.Save(first);
        store.Save(second);
        protection.ResetUnprotectCount();

        var accounts = store.GetSavedAccounts();

        Assert.Equal(1, protection.UnprotectCount);
        Assert.Equal(["account-b", "account-a"], accounts.Select(account => account.AccountId));
        Assert.True(accounts[0].IsActive);
        Assert.False(accounts[1].IsActive);
        var indexJson = protection.PlaintextSnapshots.Last(bytes =>
            Encoding.UTF8.GetString(bytes).Contains(
                PersistedSessionIndex.FormatName,
                StringComparison.Ordinal));
        var indexText = Encoding.UTF8.GetString(indexJson);
        Assert.DoesNotContain("refresh-secret-a", indexText, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret-b", indexText, StringComparison.Ordinal);
        Assert.DoesNotContain("protected-account-a", indexText, StringComparison.Ordinal);
        Assert.DoesNotContain("pin-account-a", indexText, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(store.BlobDirectoryPath, "*.bin").Length);
        Assert.All(Directory.GetFiles(store.BlobDirectoryPath, "*.bin"), path =>
            Assert.Matches("^[0-9a-f]{64}\\.[0-9a-f]{32}\\.bin$", Path.GetFileName(path)));
    }

    [Fact]
    public void ExactLoadReadsInactiveAccountWithoutChangingMru()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var first = Session("account-a", "https://a.example", "refresh-a");
        var second = Session("account-b", "https://b.example", "refresh-b");
        store.Save(first);
        store.Save(second);
        var originalIndex = File.ReadAllBytes(scope.IndexPath);

        Assert.True(store.TryLoad(
            first.AccountId,
            first.Endpoints!,
            out var loaded));

        Assert.Equal(first, loaded);
        Assert.Equal(originalIndex, File.ReadAllBytes(scope.IndexPath));
        Assert.True(store.TryLoad(out var active));
        Assert.Equal(second.AccountId, active.AccountId);
        Assert.Equal(
            [second.AccountId, first.AccountId],
            store.GetSavedAccounts().Select(account => account.AccountId));
    }

    [Fact]
    public void CorruptNonActiveBlobDoesNotAffectActiveAccountOrMetadata()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var first = Session("account-a", "https://a.example", "refresh-a");
        var second = Session("account-b", "https://b.example", "refresh-b");
        store.Save(first);
        store.Save(second);
        File.WriteAllBytes(FindBlob(store, first), [1, 2, 3]);

        Assert.True(store.TryLoad(out var active));
        Assert.Equal(second.AccountId, active.AccountId);
        Assert.Equal([second.AccountId, first.AccountId],
            store.GetSavedAccounts().Select(account => account.AccountId));
        Assert.False(store.TryActivate(first.AccountId, first.Endpoints!, out _));
        Assert.Equal([second.AccountId, first.AccountId],
            store.GetSavedAccounts().Select(account => account.AccountId));
    }

    [Fact]
    public void CopyingBlobAcrossBindingsFailsClosed()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var first = Session("account-a", "https://a.example", "refresh-a");
        var second = Session("account-b", "https://b.example", "refresh-b");
        store.Save(first);
        store.Save(second);

        File.Copy(FindBlob(store, first), FindBlob(store, second), overwrite: true);

        Assert.False(store.TryLoad(out _));
        Assert.True(store.TryActivate(first.AccountId, first.Endpoints!, out var recovered));
        Assert.Equal("refresh-a", recovered.RefreshToken);
        Assert.Equal([first.AccountId, second.AccountId],
            store.GetSavedAccounts().Select(account => account.AccountId));
    }

    [Fact]
    public void IndexCommitFailureLeavesOldRevisionAndMruUntouched()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var initial = new DpapiTokenStore(scope.IndexPath, protection);
        var session = Session("account-a", "https://a.example", "refresh-old");
        initial.Save(session);
        var originalIndex = File.ReadAllBytes(scope.IndexPath);
        try
        {
            var failing = new DpapiTokenStore(
                scope.IndexPath,
                protection,
                (kind, _) =>
                {
                    if (kind == TokenStoreWriteKind.SessionIndex)
                        throw new IOException("index commit failed");
                });

            Assert.Throws<IOException>(() =>
                failing.Save(session with { RefreshToken = "refresh-new" }));
            Assert.Equal(originalIndex, File.ReadAllBytes(scope.IndexPath));
            Assert.Single(Directory.GetFiles(initial.BlobDirectoryPath, "*.bin"));
            Assert.True(initial.TryLoad(out var loaded));
            Assert.Equal("refresh-old", loaded.RefreshToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalIndex);
        }
    }

    [Fact]
    public void LegacyCollectionReadsWithoutImplicitMigrationAndPreservesMru()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var first = Session("account-a", "https://a.example", "refresh-a");
        var second = Session("account-b", "https://b.example", "refresh-b");
        WriteLegacy(
            scope.IndexPath,
            PersistedSessionCollection.FromMostRecent([second, first]),
            AppJsonContext.Default.PersistedSessionCollection,
            protection);
        protection.ResetObservations();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var original = File.ReadAllBytes(scope.IndexPath);

        var accounts = store.GetSavedAccounts();

        Assert.Equal([second.AccountId, first.AccountId], accounts.Select(account => account.AccountId));
        Assert.Equal(original, File.ReadAllBytes(scope.IndexPath));
        Assert.False(Directory.Exists(store.BlobDirectoryPath));
        Assert.True(store.TryLoad(out var active));
        Assert.Equal(second.AccountId, active.AccountId);
        Assert.Equal("refresh-b", active.RefreshToken);
        Assert.Equal(original, File.ReadAllBytes(scope.IndexPath));
        CryptographicOperations.ZeroMemory(original);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SessionBlobRoundTripsMasterPasswordMetadata(bool hasMasterPassword)
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var session = Session("account-a", "https://a.example", "refresh-a") with
        {
            HasMasterPassword = hasMasterPassword,
        };

        store.Save(session);

        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal(hasMasterPassword, loaded.HasMasterPassword);
    }

    [Fact]
    public void SchemaSixLegacySessionMigratesMasterPasswordMetadataToUnknown()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var legacy = new PersistedSession(
            schemaVersion: 6,
            accountId: "account-a",
            endpoints: ServerEndpoints.FromWebVault("https://a.example"),
            generation: 7,
            email: "a@example.com",
            deviceIdentifier: "device-a",
            refreshToken: "refresh-a",
            protectedUserKey: "protected-a",
            kdfType: KdfType.Pbkdf2,
            kdfIterations: 600_000,
            kdfMemory: null,
            kdfParallelism: null,
            masterPasswordSalt: "a@example.com")
        {
            HasMasterPassword = true,
        };
        WriteLegacy(
            scope.IndexPath,
            legacy,
            AppJsonContext.Default.PersistedSession,
            protection);
        var store = new DpapiTokenStore(scope.IndexPath, protection);

        Assert.True(store.TryLoad(out var migrated));
        Assert.Equal(PersistedSession.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Null(migrated.HasMasterPassword);
    }

    [Fact]
    public void LegacySingleSessionReadDoesNotAttemptMigration()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var legacy = new PersistedSession(
            PersistedSession.LegacySchemaVersion,
            "account-a",
            new ServerOrigin("https://vault.bitwarden.com"),
            7,
            "a@example.com",
            "device-a",
            "refresh-a",
            "protected-a",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        WriteLegacy(
            scope.IndexPath,
            legacy,
            AppJsonContext.Default.PersistedSession,
            protection);
        var original = File.ReadAllBytes(scope.IndexPath);
        try
        {
            var failing = new DpapiTokenStore(
                scope.IndexPath,
                protection,
                (kind, _) =>
                {
                    if (kind == TokenStoreWriteKind.SessionIndex)
                        throw new IOException("migration commit failed");
                });

            Assert.True(failing.TryLoad(out var loadedFromLegacy));
            Assert.Equal(PersistedSession.CurrentSchemaVersion, loadedFromLegacy.SchemaVersion);
            Assert.Equal(original, File.ReadAllBytes(scope.IndexPath));
            Assert.False(Directory.Exists(failing.BlobDirectoryPath));

            var retry = new DpapiTokenStore(scope.IndexPath, protection);
            Assert.True(retry.TryLoad(out var loadedAgain));
            Assert.Equal("refresh-a", loadedAgain.RefreshToken);
            Assert.Equal(original, File.ReadAllBytes(scope.IndexPath));
            Assert.False(Directory.Exists(retry.BlobDirectoryPath));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
        }
    }

    [Fact]
    public void LegacyPayloadLargerThanIndexLimitReadsWithoutMigration()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var largeRefreshToken = new string('r', 120 * 1024);
        var legacy = Session("account-large", "https://large.example", largeRefreshToken);
        WriteLegacy(
            scope.IndexPath,
            legacy,
            AppJsonContext.Default.PersistedSession,
            protection);
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var original = File.ReadAllBytes(scope.IndexPath);

        Assert.True(store.TryLoad(out var migrated));
        Assert.Equal(largeRefreshToken, migrated.RefreshToken);
        Assert.Equal(original, File.ReadAllBytes(scope.IndexPath));
        Assert.False(Directory.Exists(store.BlobDirectoryPath));
        CryptographicOperations.ZeroMemory(original);
    }

    [Fact]
    public void PendingDeleteHidesAccountAndAllowsDeletionRetry()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var first = Session("account-a", "https://a.example", "refresh-a");
        var second = Session("account-b", "https://b.example", "refresh-b");
        var initial = new DpapiTokenStore(scope.IndexPath, protection);
        initial.Save(first);
        initial.Save(second);
        var firstBindingId = new PersistedAccountBinding(
            first.AccountId,
            first.Endpoints!).CreateIdentifier();
        var failing = new DpapiTokenStore(
            scope.IndexPath,
            protection,
            (kind, path) =>
            {
                if (kind == TokenStoreWriteKind.SessionBlobDelete
                    && Path.GetFileName(path).StartsWith(firstBindingId, StringComparison.Ordinal))
                {
                    throw new IOException("delete failed");
                }
            });

        Assert.False(failing.Remove(first.AccountId, first.Endpoints!));
        Assert.Equal([second.AccountId],
            initial.GetSavedAccounts().Select(account => account.AccountId));
        Assert.False(initial.TryActivate(first.AccountId, first.Endpoints!, out _));

        Assert.True(initial.Remove(first.AccountId, first.Endpoints!));
        Assert.Empty(Directory.GetFiles(initial.BlobDirectoryPath, firstBindingId + ".*.bin"));
        Assert.True(initial.TryLoad(out var active));
        Assert.Equal(second.AccountId, active.AccountId);
    }

    [Fact]
    public void DeleteAccessDeniedKeepsPendingDeleteAndRemoveReturnsFalse()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var first = Session("account-a", "https://a.example", "refresh-a");
        var second = Session("account-b", "https://b.example", "refresh-b");
        var initial = new DpapiTokenStore(scope.IndexPath, protection);
        initial.Save(first);
        initial.Save(second);
        var firstBlob = FindBlob(initial, first);
        var bindingId = new PersistedAccountBinding(
            first.AccountId,
            first.Endpoints!).CreateIdentifier();
        var deleteCalls = 0;
        var denied = new DpapiTokenStore(
            scope.IndexPath,
            protection,
            deleteFile: path =>
            {
                if (string.Equals(path, firstBlob, StringComparison.Ordinal))
                {
                    deleteCalls++;
                    throw new UnauthorizedAccessException("delete denied");
                }
                File.Delete(path);
            });

        Assert.False(denied.Remove(first.AccountId, first.Endpoints!));
        Assert.False(denied.Remove(first.AccountId, first.Endpoints!));
        Assert.True(deleteCalls >= 2);
        Assert.True(File.Exists(firstBlob));
        Assert.Equal([second.AccountId],
            denied.GetSavedAccounts().Select(account => account.AccountId));
        var latestIndexJson = protection.PlaintextSnapshots.Last(bytes =>
            Encoding.UTF8.GetString(bytes).Contains(
                PersistedSessionIndex.FormatName,
                StringComparison.Ordinal));
        var persistedIndex = JsonSerializer.Deserialize(
            latestIndexJson,
            AppJsonContext.Default.PersistedSessionIndex)!;
        Assert.Contains(persistedIndex.PendingDeletes, pending =>
            string.Equals(pending.BindingId, bindingId, StringComparison.Ordinal));

        var retry = new DpapiTokenStore(scope.IndexPath, protection);
        Assert.True(retry.Remove(first.AccountId, first.Endpoints!));
        Assert.False(File.Exists(firstBlob));
    }

    [Fact]
    public void RemoveDeletesCrashTemporaryBlobWhenNoCommittedStateExists()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var session = Session("account-a", "https://a.example", "refresh-a");
        var temporaryPath = WriteCrashTemporaryBlob(store, session, "temporary-secret");

        Assert.True(store.Remove(session.AccountId, session.Endpoints!));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public void RemoveDeletesCommittedBlobAndCrashTemporaryBlobForBinding()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var session = Session("account-a", "https://a.example", "refresh-a");
        store.Save(session);
        var committedPath = FindBlob(store, session);
        var temporaryPath = WriteCrashTemporaryBlob(store, session, "temporary-secret");

        Assert.True(store.Remove(session.AccountId, session.Endpoints!));
        Assert.False(File.Exists(committedPath));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public void RemovePreservesOtherBindingsAndMalformedTemporaryFiles()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var target = Session("account-a", "https://a.example", "refresh-a");
        var other = Session("account-b", "https://b.example", "refresh-b");
        var targetTemporaryPath = WriteCrashTemporaryBlob(store, target, "target-secret");
        var otherTemporaryPath = WriteCrashTemporaryBlob(store, other, "other-secret");
        var malformedPath = WriteCrashTemporaryBlob(
            store,
            target,
            "malformed-secret",
            processId: "not-a-process-id");

        Assert.True(store.Remove(target.AccountId, target.Endpoints!));
        Assert.False(File.Exists(targetTemporaryPath));
        Assert.True(File.Exists(otherTemporaryPath));
        Assert.True(File.Exists(malformedPath));
    }

    [Fact]
    public void RemoveReportsTemporaryDeletionFailureWithoutDeletingOtherBinding()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var target = Session("account-a", "https://a.example", "refresh-a");
        var other = Session("account-b", "https://b.example", "refresh-b");
        var seed = new DpapiTokenStore(scope.IndexPath, protection);
        var targetTemporaryPath = WriteCrashTemporaryBlob(seed, target, "target-secret");
        var otherTemporaryPath = WriteCrashTemporaryBlob(seed, other, "other-secret");
        var denied = new DpapiTokenStore(
            scope.IndexPath,
            protection,
            deleteFile: path =>
            {
                if (string.Equals(path, targetTemporaryPath, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("temporary delete denied");
                File.Delete(path);
            });

        Assert.False(denied.Remove(target.AccountId, target.Endpoints!));
        Assert.True(File.Exists(targetTemporaryPath));
        Assert.True(File.Exists(otherTemporaryPath));
    }

    [Fact]
    public void SaveFailsClosedBeforeCreatingStoreFilesWhenWriteGuardRejects()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var guard = new TestWriteGuard(allow: false);
        var store = new DpapiTokenStore(scope.IndexPath, protection, guard);
        var session = Session("account-a", "https://a.example", "refresh-a");

        Assert.Throws<AccountDeletionWriteBlockedException>(() => store.Save(session));
        Assert.Equal(1, guard.AcquireCount);
        Assert.Equal(session.AccountId, guard.LastAccountId);
        Assert.Equal(session.Endpoints, guard.LastEndpoints);
        Assert.False(File.Exists(scope.IndexPath));
        Assert.False(Directory.Exists(store.BlobDirectoryPath));
    }

    [Fact]
    public void SaveHoldsWriteGuardLeaseThroughCommitAndReleasesIt()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var guard = new TestWriteGuard(allow: true);
        var observedWrites = new List<TokenStoreWriteKind>();
        var store = new DpapiTokenStore(
            scope.IndexPath,
            protection,
            guard,
            (kind, _) =>
            {
                Assert.Equal(1, guard.ActiveLeaseCount);
                observedWrites.Add(kind);
            });

        store.Save(Session("account-a", "https://a.example", "refresh-a"));

        Assert.Contains(TokenStoreWriteKind.SessionBlob, observedWrites);
        Assert.Contains(TokenStoreWriteKind.SessionIndex, observedWrites);
        Assert.Equal(0, guard.ActiveLeaseCount);
        Assert.Equal(1, guard.ReleaseCount);
    }

    [Fact]
    public void SaveReleasesWriteGuardLeaseWhenCommitFails()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var guard = new TestWriteGuard(allow: true);
        var store = new DpapiTokenStore(
            scope.IndexPath,
            protection,
            guard,
            (kind, _) =>
            {
                Assert.Equal(1, guard.ActiveLeaseCount);
                if (kind == TokenStoreWriteKind.SessionIndex)
                    throw new IOException("index commit failed");
            });

        Assert.Throws<IOException>(() =>
            store.Save(Session("account-a", "https://a.example", "refresh-a")));
        Assert.Equal(0, guard.ActiveLeaseCount);
        Assert.Equal(1, guard.ReleaseCount);
    }

    [Fact]
    public void RemoveDoesNotConsultRejectingWriteGuard()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var session = Session("account-a", "https://a.example", "refresh-a");
        new DpapiTokenStore(scope.IndexPath, protection).Save(session);
        var guard = new TestWriteGuard(allow: false);
        var blockedWriter = new DpapiTokenStore(scope.IndexPath, protection, guard);

        Assert.True(blockedWriter.Remove(session.AccountId, session.Endpoints!));
        Assert.Equal(0, guard.AcquireCount);
        Assert.Empty(blockedWriter.GetSavedAccounts());
    }

    [Fact]
    public void TryActivateFailsClosedBeforeTakingStoreLockWhenWriteGuardRejects()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var session = Session("account-a", "https://a.example", "refresh-a");
        new DpapiTokenStore(scope.IndexPath, protection).Save(session);
        var guard = new TestWriteGuard(allow: false);
        var blocked = new DpapiTokenStore(scope.IndexPath, protection, guard);

        Assert.Throws<AccountDeletionWriteBlockedException>(() =>
            blocked.TryActivate(session.AccountId, session.Endpoints!, out _));
        Assert.Equal(1, guard.AcquireCount);
        Assert.True(new DpapiTokenStore(scope.IndexPath, protection).TryLoad(out var loaded));
        Assert.Equal(session.RefreshToken, loaded.RefreshToken);
    }

    [Fact]
    public void IndexReadAccessDeniedCannotBeTreatedAsAnEmptyWritableStore()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var initial = new DpapiTokenStore(scope.IndexPath, protection);
        initial.Save(Session("account-a", "https://a.example", "refresh-a"));
        var originalIndex = File.ReadAllBytes(scope.IndexPath);
        try
        {
            byte[] Reader(string path) => string.Equals(
                path,
                scope.IndexPath,
                StringComparison.Ordinal)
                    ? throw new UnauthorizedAccessException("index read denied")
                    : File.ReadAllBytes(path);
            var denied = new DpapiTokenStore(
                scope.IndexPath,
                protection,
                reader: Reader);

            Assert.Empty(denied.GetSavedAccounts());
            Assert.False(denied.TryLoad(out _));
            Assert.Throws<UnauthorizedAccessException>(() =>
                denied.Save(Session("account-b", "https://b.example", "refresh-b")));
            Assert.Equal(originalIndex, File.ReadAllBytes(scope.IndexPath));
            Assert.Single(Directory.GetFiles(initial.BlobDirectoryPath, "*.bin"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalIndex);
        }
    }

    [Fact]
    public void ProductionStoreDoesNotUseExistenceProbesForSecurityDecisions()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src",
            "App",
            "Services",
            "DpapiTokenStore.cs"));

        Assert.DoesNotContain("File.Exists(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearIndexCommitFailureLeavesEveryAccountReadable()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var initial = new DpapiTokenStore(scope.IndexPath, protection);
        initial.Save(Session("account-a", "https://a.example", "refresh-a"));
        initial.Save(Session("account-b", "https://b.example", "refresh-b"));
        var originalIndex = File.ReadAllBytes(scope.IndexPath);
        try
        {
            var failing = new DpapiTokenStore(
                scope.IndexPath,
                protection,
                (kind, _) =>
                {
                    if (kind == TokenStoreWriteKind.SessionIndex)
                        throw new IOException("clear index commit failed");
                });

            Assert.Throws<IOException>(() => failing.Clear());
            Assert.Equal(originalIndex, File.ReadAllBytes(scope.IndexPath));
            Assert.Equal(2, Directory.GetFiles(initial.BlobDirectoryPath, "*.bin").Length);
            Assert.True(initial.TryLoad(out var active));
            Assert.Equal("account-b", active.AccountId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(originalIndex);
        }
    }

    [Fact]
    public void ClearBlobDeletionFailureCommitsEmptyIndexAndCanRetry()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var initial = new DpapiTokenStore(scope.IndexPath, protection);
        initial.Save(Session("account-a", "https://a.example", "refresh-a"));
        initial.Save(Session("account-b", "https://b.example", "refresh-b"));
        var failing = new DpapiTokenStore(
            scope.IndexPath,
            protection,
            (kind, _) =>
            {
                if (kind == TokenStoreWriteKind.SessionBlobDelete)
                    throw new IOException("clear blob delete failed");
            });

        Assert.Throws<IOException>(() => failing.Clear());
        Assert.Equal(2, Directory.GetFiles(initial.BlobDirectoryPath, "*.bin").Length);
        Assert.Empty(failing.GetSavedAccounts());
        Assert.False(failing.TryLoad(out _));

        var retry = new DpapiTokenStore(scope.IndexPath, protection);
        retry.Clear();
        Assert.False(File.Exists(scope.IndexPath));
        Assert.False(Directory.Exists(retry.BlobDirectoryPath));
    }

    [Fact]
    public void StoreCapsMruAtFiveAndKeepsSameAccountOnDifferentEndpoints()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        store.Save(Session("same-account", "https://first.example", "first"));
        store.Save(Session("same-account", "https://second.example", "second"));
        foreach (var index in Enumerable.Range(0, 4))
            store.Save(Session($"account-{index}", $"https://{index}.example", $"refresh-{index}"));

        var accounts = store.GetSavedAccounts();

        Assert.Equal(5, accounts.Count);
        Assert.Equal("account-3", accounts[0].AccountId);
        Assert.Single(accounts, account =>
            account.AccountId == "same-account"
            && account.Endpoints.WebVault.Value == "https://second.example");
        Assert.DoesNotContain(accounts, account =>
            account.AccountId == "same-account"
            && account.Endpoints.WebVault.Value == "https://first.example");
        Assert.Equal(5, Directory.GetFiles(store.BlobDirectoryPath, "*.bin").Length);
    }

    [Fact]
    public async Task TwoStoreInstancesSerializeReadModifyWriteWithLockFile()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var firstStore = new DpapiTokenStore(scope.IndexPath, protection);
        var secondStore = new DpapiTokenStore(scope.IndexPath, protection);

        await Task.WhenAll(
            Task.Run(() => firstStore.Save(Session("account-a", "https://a.example", "a"))),
            Task.Run(() => secondStore.Save(Session("account-b", "https://b.example", "b"))));

        Assert.Equal(2, firstStore.GetSavedAccounts().Count);
        Assert.Equal(
            ["account-a", "account-b"],
            firstStore.GetSavedAccounts().Select(account => account.AccountId).Order().ToArray());
    }

    [Fact]
    public void OversizedIndexFailsClosedBeforeUnprotect()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.IndexPath)!);
        var bytes = new byte[129 * 1024];
        "WVWIDX01"u8.CopyTo(bytes);
        File.WriteAllBytes(scope.IndexPath, bytes);
        CryptographicOperations.ZeroMemory(bytes);
        var store = new DpapiTokenStore(scope.IndexPath, protection);

        Assert.Empty(store.GetSavedAccounts());
        Assert.Equal(0, protection.UnprotectCount);
    }

    [Fact]
    public void DamagedNewFormatMagicDoesNotFallBackToLegacyParsing()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        store.Save(Session("account-a", "https://a.example", "refresh-a"));
        var bytes = File.ReadAllBytes(scope.IndexPath);
        try
        {
            bytes[0] ^= 0x01;
            File.WriteAllBytes(scope.IndexPath, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        protection.ResetUnprotectCount();

        Assert.Empty(store.GetSavedAccounts());
        Assert.False(store.TryLoad(out _));
        Assert.Equal(2, protection.UnprotectCount);
    }

    [Fact]
    public void StoreClearsProtectorInputOutputAndEntropyBuffers()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        store.Save(Session("account-a", "https://a.example", "refresh-a"));
        Assert.True(store.TryLoad(out _));

        Assert.NotEmpty(protection.ObservedOwnedBuffers);
        Assert.All(protection.ObservedOwnedBuffers, buffer =>
            Assert.All(buffer, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void CleanupRemovesOnlyOwnedFilesFromPathSpecificBlobDirectory()
    {
        using var scope = new StoreScope();
        using var protection = new TestProtectionPolicy();
        var store = new DpapiTokenStore(scope.IndexPath, protection);
        var session = Session("account-a", "https://a.example", "refresh-a");
        store.Save(session);
        var ownedBlobName = Path.GetFileName(FindBlob(store, session));
        var ownedTemporaryPath = Path.Combine(
            store.BlobDirectoryPath,
            $".{ownedBlobName}.123.{Guid.NewGuid():N}.tmp");
        var unrelatedPath = Path.Combine(store.BlobDirectoryPath, "unrelated.bin");
        var legacyTemporaryPath = scope.IndexPath + ".tmp";
        File.WriteAllBytes(ownedTemporaryPath, [1]);
        File.WriteAllBytes(unrelatedPath, [2]);
        File.WriteAllBytes(legacyTemporaryPath, [3]);

        Assert.Single(store.GetSavedAccounts());
        Assert.False(File.Exists(ownedTemporaryPath));
        Assert.False(File.Exists(legacyTemporaryPath));
        Assert.True(File.Exists(unrelatedPath));

        store.Clear();
        Assert.False(File.Exists(scope.IndexPath));
        Assert.True(File.Exists(unrelatedPath));
    }

    private static PersistedSession Session(
        string accountId,
        string serverUrl,
        string refreshToken) => new(
        PersistedSession.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        $"device-{accountId}",
        refreshToken,
        $"protected-{accountId}",
        KdfType.Pbkdf2,
        600_000,
        null,
        null)
    {
        PinProtectedUserKey = $"pin-{accountId}",
        TwoFactorToken = $"two-factor-{accountId}",
        HelloProtectedUserKey = $"hello-{accountId}",
        HelloChallenge = $"challenge-{accountId}",
    };

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join('/', relativeParts));
    }

    private static string FindBlob(DpapiTokenStore store, PersistedSession session)
    {
        var bindingId = new PersistedAccountBinding(
            session.AccountId,
            session.Endpoints!).CreateIdentifier();
        return Assert.Single(Directory.GetFiles(
            store.BlobDirectoryPath,
            bindingId + ".*.bin"));
    }

    private static string WriteCrashTemporaryBlob(
        DpapiTokenStore store,
        PersistedSession session,
        string contents,
        string processId = "123")
    {
        var bindingId = new PersistedAccountBinding(
            session.AccountId,
            session.Endpoints!).CreateIdentifier();
        var path = Path.Combine(
            store.BlobDirectoryPath,
            $".{bindingId}.{Guid.NewGuid():N}.bin.{processId}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(store.BlobDirectoryPath);
        File.WriteAllText(path, contents);
        return path;
    }

    private static void WriteLegacy<T>(
        string path,
        T value,
        JsonTypeInfo<T> typeInfo,
        TestProtectionPolicy protection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = protection.Protect(jsonBytes, entropy: null);
            File.WriteAllBytes(path, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private sealed class StoreScope : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));

        public string IndexPath => Path.Combine(_root, "session.bin");

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestWriteGuard(bool allow) : IAccountDeletionLocalWriteGuard
    {
        public int AcquireCount { get; private set; }

        public int ActiveLeaseCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public string? LastAccountId { get; private set; }

        public ServerEndpoints? LastEndpoints { get; private set; }

        public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints)
        {
            AcquireCount++;
            LastAccountId = accountId;
            LastEndpoints = endpoints;
            if (!allow)
                return null;

            ActiveLeaseCount++;
            return new TestLease(this);
        }

        private sealed class TestLease(TestWriteGuard owner) : IDisposable
        {
            private TestWriteGuard? _owner = owner;

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref _owner, null);
                if (current is null)
                    return;
                current.ActiveLeaseCount--;
                current.ReleaseCount++;
            }
        }
    }

    private sealed class TestProtectionPolicy : ITokenStoreProtectionPolicy, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _plaintextSnapshots = [];
        private readonly List<byte[]> _observedOwnedBuffers = [];
        private int _unprotectCount;

        public bool IsSupported => true;

        public int UnprotectCount
        {
            get
            {
                lock (_gate)
                    return _unprotectCount;
            }
        }

        public IReadOnlyList<byte[]> PlaintextSnapshots
        {
            get
            {
                lock (_gate)
                    return _plaintextSnapshots.ToArray();
            }
        }

        public IReadOnlyList<byte[]> ObservedOwnedBuffers
        {
            get
            {
                lock (_gate)
                    return _observedOwnedBuffers.ToArray();
            }
        }

        public byte[] Protect(byte[] plaintext, byte[]? entropy)
        {
            var fingerprint = EntropyFingerprint(entropy);
            try
            {
                var protectedBytes = new byte[fingerprint.Length + plaintext.Length];
                fingerprint.CopyTo(protectedBytes, 0);
                for (var index = 0; index < plaintext.Length; index++)
                {
                    protectedBytes[fingerprint.Length + index] =
                        (byte)(plaintext[index] ^ fingerprint[index % fingerprint.Length]);
                }

                lock (_gate)
                {
                    _plaintextSnapshots.Add(plaintext.ToArray());
                    _observedOwnedBuffers.Add(plaintext);
                    if (entropy is not null)
                        _observedOwnedBuffers.Add(entropy);
                    _observedOwnedBuffers.Add(protectedBytes);
                }
                return protectedBytes;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fingerprint);
            }
        }

        public byte[] Unprotect(byte[] protectedData, byte[]? entropy)
        {
            var fingerprint = EntropyFingerprint(entropy);
            try
            {
                lock (_gate)
                {
                    _unprotectCount++;
                    _observedOwnedBuffers.Add(protectedData);
                    if (entropy is not null)
                        _observedOwnedBuffers.Add(entropy);
                }

                if (protectedData.Length <= fingerprint.Length
                    || !CryptographicOperations.FixedTimeEquals(
                        protectedData.AsSpan(0, fingerprint.Length),
                        fingerprint))
                {
                    throw new CryptographicException("Protection entropy does not match.");
                }

                var plaintext = new byte[protectedData.Length - fingerprint.Length];
                for (var index = 0; index < plaintext.Length; index++)
                {
                    plaintext[index] = (byte)(
                        protectedData[fingerprint.Length + index]
                        ^ fingerprint[index % fingerprint.Length]);
                }
                lock (_gate)
                    _observedOwnedBuffers.Add(plaintext);
                return plaintext;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(fingerprint);
            }
        }

        public void ResetUnprotectCount()
        {
            lock (_gate)
                _unprotectCount = 0;
        }

        public void ResetObservations()
        {
            lock (_gate)
            {
                foreach (var snapshot in _plaintextSnapshots)
                    CryptographicOperations.ZeroMemory(snapshot);
                _plaintextSnapshots.Clear();
                _observedOwnedBuffers.Clear();
                _unprotectCount = 0;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var snapshot in _plaintextSnapshots)
                    CryptographicOperations.ZeroMemory(snapshot);
                _plaintextSnapshots.Clear();
                _observedOwnedBuffers.Clear();
            }
        }

        private static byte[] EntropyFingerprint(byte[]? entropy) =>
            SHA256.HashData(entropy is null
                ? ReadOnlySpan<byte>.Empty
                : entropy);
    }
}
