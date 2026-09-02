using System.Net;
using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AccountDeletionServiceTests
{
    private const string MasterPassword = "correct horse battery staple";

    [Fact]
    public async Task MasterPasswordDeletion_PreparesBeforeDispatch_ThenCleansExactBinding()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.Api.DeleteAccount = (_, _) =>
        {
            fixture.Events.Add("api:delete");
            return Task.CompletedTask;
        };

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.Confirmed>(result);
        Assert.Equal(
            ["tombstone:prepared", "api:delete", "tombstone:server-confirmed"],
            fixture.Events.Take(3));
        Assert.NotNull(fixture.Api.AccountDeletion?.MasterPasswordHash);
        Assert.Null(fixture.Api.AccountDeletion?.Otp);
        Assert.Equal(fixture.ExpectedMasterPasswordHash(), fixture.Api.AccountDeletion?.MasterPasswordHash);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.False(fixture.TokenStore.Contains(fixture.Persisted));
        Assert.False(fixture.CacheStore.Contains(fixture.Persisted.AccountId, fixture.Endpoints));
        Assert.False(fixture.DeviceKeyStore.Contains(fixture.DeviceBinding));
        Assert.Equal(
            AccountDeletionStage.LocalCleanupComplete,
            fixture.ReadTombstone().Stage);
    }

    [Fact]
    public async Task PasswordlessAccount_RequestsAndDispatchesOnlyOneTimePassword()
    {
        using var fixture = new Fixture(hasMasterPassword: false);

        var otpResult = await fixture.Service.RequestOtpAsync(
            TestContext.Current.CancellationToken);
        var deletion = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromOneTimePassword(" 123456 "),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionOtpRequestResult.Sent>(otpResult);
        Assert.IsType<AccountDeletionResult.Confirmed>(deletion);
        Assert.Equal(1, fixture.Api.AccountDeletionOtpRequestCount);
        Assert.Equal(1, fixture.Api.AccountDeletionRequestCount);
        Assert.Null(fixture.Api.AccountDeletion?.MasterPasswordHash);
        Assert.Equal("123456", fixture.Api.AccountDeletion?.Otp);
    }

    [Fact]
    public async Task UnknownMasterPasswordCapability_FailsClosedWithoutOtpOrDelete()
    {
        using var fixture = new Fixture(hasMasterPassword: null);

        var otp = await fixture.Service.RequestOtpAsync(TestContext.Current.CancellationToken);
        var deletion = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountDeletionRejectionReason.FreshOnlineAuthenticationRequired,
            Assert.IsType<AccountDeletionOtpRequestResult.Rejected>(otp).Reason);
        Assert.Equal(
            AccountDeletionRejectionReason.FreshOnlineAuthenticationRequired,
            Assert.IsType<AccountDeletionResult.Rejected>(deletion).Reason);
        Assert.Equal(0, fixture.Api.AccountDeletionOtpRequestCount);
        Assert.Equal(0, fixture.Api.AccountDeletionRequestCount);
        Assert.Equal(
            AccountDeletionTombstoneReadStatus.Absent,
            fixture.Tombstones.Read(fixture.Binding).Status);
    }

    [Fact]
    public async Task ExplicitlyClaimedAccount_IsRejectedBeforeDurablePrepare()
    {
        using var fixture = new Fixture(
            hasMasterPassword: true,
            claimedByOrganization: true);

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountDeletionRejectionReason.ClaimedByOrganization,
            Assert.IsType<AccountDeletionResult.Rejected>(result).Reason);
        Assert.Equal(0, fixture.Api.AccountDeletionRequestCount);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task ConfirmedOwnerMembership_IsWarningButServerRemainsAuthority()
    {
        var owner = new VaultOrganization("org-1", "Team", 2, 0, true)
        {
            ProductTierType = 2,
        };
        using var fixture = new Fixture(
            hasMasterPassword: true,
            organizations: [owner]);
        fixture.Api.DeleteAccount = (_, _) => Task.FromException(
            new VaultWriteException(HttpStatusCode.BadRequest, "Owner must transfer the organization.", null));

        var preflight = fixture.Service.GetPreflight();
        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var warning = Assert.Single(preflight.OwnerWarnings);
        Assert.Equal("org-1", warning.OrganizationId);
        Assert.Equal(2, warning.ProductTierType);
        Assert.Equal(
            AccountDeletionRejectionReason.ServerRejected,
            Assert.IsType<AccountDeletionResult.Rejected>(result).Reason);
        Assert.Equal(1, fixture.Api.AccountDeletionRequestCount);
        Assert.Equal(
            AccountDeletionTombstoneReadStatus.Absent,
            fixture.Tombstones.Read(fixture.Binding).Status);
    }

    [Fact]
    public async Task CallerCancellationBeforeDispatch_RemovesExactPreparedTombstone()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Tombstones.OnCreate = cancellation.Cancel;

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            cancellation.Token);

        Assert.IsType<AccountDeletionResult.CancelledBeforeDispatch>(result);
        Assert.Equal(0, fixture.Api.AccountDeletionRequestCount);
        Assert.Equal(
            AccountDeletionTombstoneReadStatus.Absent,
            fixture.Tombstones.Read(fixture.Binding).Status);
        Assert.True(fixture.TokenStore.Contains(fixture.Persisted));
        Assert.Equal(
            AccountDeletionTombstoneRemovalReason.CancelledBeforeDispatch,
            fixture.Tombstones.LastRemovalReason);
    }

    [Fact]
    public async Task CancellationWhileWaitingForCoordinationNeverPreparesOrDispatches()
    {
        var gate = new PausingAccountDeletionCoordinationGate();
        using var fixture = new Fixture(hasMasterPassword: true, coordinationGate: gate);
        using var cancellation = new CancellationTokenSource();

        var deletion = fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            cancellation.Token);
        await gate.AcquireStarted.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Api.AccountDeletionRequestCount);

        cancellation.Cancel();
        var result = await deletion;

        Assert.IsType<AccountDeletionResult.CancelledBeforeDispatch>(result);
        Assert.Equal(
            AccountDeletionTombstoneReadStatus.Absent,
            fixture.Tombstones.Read(fixture.Binding).Status);
    }

    [Fact]
    public async Task CoordinationWaitTimeoutNeverPreparesOrDispatches()
    {
        var gate = new PausingAccountDeletionCoordinationGate();
        using var fixture = new Fixture(
            hasMasterPassword: true,
            coordinationGate: gate,
            coordinationWaitTimeout: TimeSpan.FromMilliseconds(50));

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.CancelledBeforeDispatch>(result);
        Assert.Empty(fixture.Events);
        Assert.Equal(0, fixture.Api.AccountDeletionRequestCount);
        Assert.Equal(
            AccountDeletionTombstoneReadStatus.Absent,
            fixture.Tombstones.Read(fixture.Binding).Status);
    }

    [Fact]
    public async Task CallerCancellationAfterDispatch_RetainsPreparedAndOnlyQuarantinesMemory()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.DeleteAccount = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var deletion = fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var result = await deletion;

        Assert.Equal(
            AccountDeletionUncertainReason.CallerCancelledAfterDispatch,
            Assert.IsType<AccountDeletionResult.OutcomeUncertain>(result).Reason);
        Assert.Equal(AccountDeletionStage.Prepared, fixture.ReadTombstone().Stage);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.True(fixture.TokenStore.Contains(fixture.Persisted));
        Assert.True(fixture.CacheStore.Contains(fixture.Persisted.AccountId, fixture.Endpoints));
        Assert.True(fixture.DeviceKeyStore.Contains(fixture.DeviceBinding));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UncertainHttpStatus_RetainsPreparedWithoutPersistentPurge(
        HttpStatusCode statusCode)
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.Api.DeleteAccount = (_, _) => Task.FromException(
            new VaultWriteException(statusCode, "uncertain", null));

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.OutcomeUncertain>(result);
        Assert.Equal(AccountDeletionStage.Prepared, fixture.ReadTombstone().Stage);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.True(fixture.TokenStore.Contains(fixture.Persisted));
    }

    [Fact]
    public async Task UnexpectedSuccessfulHttpStatus_RetainsPreparedAsOutcomeUncertain()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.Api.DeleteAccount = (_, _) => Task.FromException(
            new AccountDeletionOutcomeUncertainException(HttpStatusCode.Accepted));

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var uncertain = Assert.IsType<AccountDeletionResult.OutcomeUncertain>(result);
        Assert.Equal(
            AccountDeletionUncertainReason.ServerReturnedUncertainStatus,
            uncertain.Reason);
        Assert.Equal(AccountDeletionStage.Prepared, fixture.ReadTombstone().Stage);
        Assert.True(fixture.TokenStore.Contains(fixture.Persisted));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task DefiniteServerRejection_RemovesPrepared(HttpStatusCode statusCode)
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.Api.DeleteAccount = (_, _) => Task.FromException(
            new VaultWriteException(statusCode, "rejected", null));

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var rejected = Assert.IsType<AccountDeletionResult.Rejected>(result);
        Assert.Equal(AccountDeletionRejectionReason.ServerRejected, rejected.Reason);
        Assert.Equal((int)statusCode, rejected.HttpStatusCode);
        Assert.Equal(
            AccountDeletionTombstoneReadStatus.Absent,
            fixture.Tombstones.Read(fixture.Binding).Status);
        Assert.Equal(VaultState.Unlocked, fixture.Session.State);
        Assert.True(fixture.TokenStore.Contains(fixture.Persisted));
    }

    [Fact]
    public async Task ServerConfirmationWithAdvanceFailure_StillPurgesEveryLocalTarget()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.Tombstones.FailAdvance = true;

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var incomplete = Assert.IsType<AccountDeletionResult.ConfirmedLocalCleanupIncomplete>(result);
        Assert.Contains(
            incomplete.Failures,
            failure => failure.Target == AccountDeletionCleanupTarget.Tombstone);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.False(fixture.TokenStore.Contains(fixture.Persisted));
        Assert.False(fixture.CacheStore.Contains(fixture.Persisted.AccountId, fixture.Endpoints));
        Assert.False(fixture.DeviceKeyStore.Contains(fixture.DeviceBinding));
        Assert.Equal(AccountDeletionStage.Prepared, fixture.ReadTombstone().Stage);
        Assert.Equal(
            (fixture.Persisted.AccountId, fixture.Endpoints),
            fixture.ApprovalStore.LastPurgedScope);
        Assert.Equal(
            (fixture.Persisted.AccountId, fixture.Endpoints),
            fixture.TdeStore.LastPurgedScope);
    }

    [Fact]
    public async Task OneCleanupFailure_DoesNotSkipRemainingTargets_AndResumeCompletes()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.TokenStore.ThrowOnRemove = true;

        var first = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var incomplete = Assert.IsType<AccountDeletionResult.ConfirmedLocalCleanupIncomplete>(first);
        Assert.Contains(
            incomplete.Failures,
            failure => failure.Target == AccountDeletionCleanupTarget.Token);
        Assert.Equal(AccountDeletionStage.ServerConfirmed, fixture.ReadTombstone().Stage);
        Assert.False(fixture.CacheStore.Contains(fixture.Persisted.AccountId, fixture.Endpoints));
        Assert.False(fixture.DeviceKeyStore.Contains(fixture.DeviceBinding));
        Assert.NotNull(fixture.ApprovalStore.LastPurgedScope);
        Assert.NotNull(fixture.TdeStore.LastPurgedScope);

        fixture.TokenStore.ThrowOnRemove = false;
        var resumed = await fixture.Service.ResumeConfirmedCleanupAsync(
            fixture.ReadTombstone(),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.Confirmed>(resumed);
        Assert.Equal(
            AccountDeletionStage.LocalCleanupComplete,
            fixture.ReadTombstone().Stage);
    }

    [Fact]
    public async Task CacheRemovalUnavailable_RetainsServerConfirmedMarkerAndReportsFailure()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.CacheStore.RemovalOverride = VaultCacheRemovalStatus.Unavailable;

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var incomplete = Assert.IsType<AccountDeletionResult.ConfirmedLocalCleanupIncomplete>(
            result);
        Assert.Contains(
            incomplete.Failures,
            failure => failure.Target == AccountDeletionCleanupTarget.VaultCache
                && failure.Kind == AccountDeletionCleanupFailureKind.Unavailable);
        Assert.Equal(AccountDeletionStage.ServerConfirmed, fixture.ReadTombstone().Stage);
    }

    [Fact]
    public async Task FaviconRemovalUnavailable_RetainsServerConfirmedMarkerAndReportsFailure()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.FaviconCache.Status = FaviconCacheClearStatus.Unavailable;

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var incomplete = Assert.IsType<AccountDeletionResult.ConfirmedLocalCleanupIncomplete>(
            result);
        Assert.Contains(
            incomplete.Failures,
            failure => failure.Target == AccountDeletionCleanupTarget.FaviconCache
                && failure.Kind == AccountDeletionCleanupFailureKind.Unavailable);
        Assert.Equal(AccountDeletionStage.ServerConfirmed, fixture.ReadTombstone().Stage);
    }

    [Fact]
    public async Task PreparedRecovery_NeverRedispatches_ExplicitLocalClearPurgesExactDevice()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        var prepared = fixture.Tombstones.TryCreate(fixture.Binding, DateTimeOffset.UtcNow)!;

        var resumed = await fixture.Service.ResumeConfirmedCleanupAsync(
            prepared,
            TestContext.Current.CancellationToken);
        var cleared = await fixture.Service.ClearLocalDataForPendingAsync(
            prepared,
            TestContext.Current.CancellationToken);
        var marker = Assert.IsType<AccountDeletionTombstoneRecord>(cleared.Pending);
        var resumedAfterClear = await fixture.Service.ResumeConfirmedCleanupAsync(
            marker,
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.OutcomeUncertain>(resumed);
        Assert.IsType<AccountDeletionResult.OutcomeUncertainLocalCleanupComplete>(
            resumedAfterClear);
        Assert.Equal(0, fixture.Api.AccountDeletionRequestCount);
        Assert.Equal(AccountDeletionLocalResolutionStatus.Completed, cleared.Status);
        Assert.Equal(VaultState.LoggedOut, fixture.Session.State);
        Assert.False(fixture.TokenStore.Contains(fixture.Persisted));
        Assert.Equal(
            AccountDeletionStage.UncertainLocalCleanupComplete,
            fixture.ReadTombstone().Stage);
        Assert.Null(fixture.Tombstones.LastRemovalReason);
    }

    [Fact]
    public async Task ConfirmedCleanup_PurgesEveryDeviceKeyInAccountScope()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        var oldDevice = new DeviceKeyBinding(
            fixture.Persisted.AccountId,
            fixture.Endpoints,
            "55555555-5555-5555-5555-555555555555");
        fixture.DeviceKeyStore.Save(oldDevice, Enumerable.Repeat((byte)8, 64).ToArray());

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.Confirmed>(result);
        Assert.False(fixture.DeviceKeyStore.Contains(fixture.DeviceBinding));
        Assert.False(fixture.DeviceKeyStore.Contains(oldDevice));
    }

    [Fact]
    public async Task OutcomeUncertain_QuarantinesNewestMatchingSessionGeneration()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        fixture.Api.DeleteAccount = (_, _) =>
        {
            var newer = fixture.Session.BeginAccountSession(
                fixture.Persisted.AccountId,
                fixture.Persisted.Email,
                fixture.Endpoints);
            fixture.Session.SetTokens("new-access", "new-refresh");
            fixture.Session.SetState(VaultState.Unlocked);
            Assert.True(fixture.Session.IsCurrent(newer));
            return Task.FromException(new HttpRequestException("transport"));
        };

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.OutcomeUncertain>(result);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
        Assert.Equal(AccountDeletionStage.Prepared, fixture.ReadTombstone().Stage);
    }

    [Fact]
    public async Task ConfirmedCleanup_DoesNotRemoveAnotherAccountOnSameServer()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        var other = fixture.AddOtherAccount();

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.Confirmed>(result);
        Assert.False(fixture.TokenStore.Contains(fixture.Persisted));
        Assert.True(fixture.TokenStore.Contains(other.Session));
        Assert.False(fixture.CacheStore.Contains(fixture.Persisted.AccountId, fixture.Endpoints));
        Assert.True(fixture.CacheStore.Contains(other.Session.AccountId, other.Session.Endpoints!));
        Assert.False(fixture.DeviceKeyStore.Contains(fixture.DeviceBinding));
        Assert.True(fixture.DeviceKeyStore.Contains(other.DeviceBinding));
        Assert.All(
            fixture.TokenStore.RemovedBindings,
            removed => Assert.Equal(fixture.Persisted.AccountId, removed.AccountId));
        Assert.DoesNotContain(
            fixture.CacheStore.RemovedBindings,
            removed => removed.AccountId == other.Session.AccountId);
    }

    [Fact]
    public async Task OneTimePasswordCredential_IsConsumedAndNeverAutomaticallyRetried()
    {
        using var fixture = new Fixture(hasMasterPassword: false);
        fixture.Api.DeleteAccount = (_, _) => Task.FromException(
            new HttpRequestException("transport"));
        var credential = AccountDeletionCredential.FromOneTimePassword("123456");

        var first = await fixture.Service.DeleteAsync(
            credential,
            TestContext.Current.CancellationToken);
        var second = await fixture.Service.DeleteAsync(
            credential,
            TestContext.Current.CancellationToken);

        Assert.IsType<AccountDeletionResult.OutcomeUncertain>(first);
        Assert.Equal(
            AccountDeletionRejectionReason.RequiresUnlockedSession,
            Assert.IsType<AccountDeletionResult.Rejected>(second).Reason);
        Assert.Equal(1, fixture.Api.AccountDeletionRequestCount);
    }

    [Fact]
    public async Task ChangedEmailStillFindsPreparedAccountScopeAndBlocksRedispatch()
    {
        using var fixture = new Fixture(hasMasterPassword: true);
        var oldEmailBinding = new AccountDeletionBinding(
            fixture.Binding.AccountId,
            "old-email@example.com",
            fixture.Endpoints,
            fixture.Binding.DeviceIdentifier);
        fixture.Tombstones.TryCreate(oldEmailBinding, DateTimeOffset.UtcNow);

        var result = await fixture.Service.DeleteAsync(
            AccountDeletionCredential.FromMasterPassword(MasterPassword),
            TestContext.Current.CancellationToken);

        var uncertain = Assert.IsType<AccountDeletionResult.OutcomeUncertain>(result);
        Assert.Equal(oldEmailBinding, uncertain.Pending.Binding);
        Assert.Equal(0, fixture.Api.AccountDeletionRequestCount);
        Assert.Equal(VaultState.Locked, fixture.Session.State);
    }

    private sealed class Fixture : IDisposable
    {
        private const string AccountId = "11111111-1111-1111-1111-111111111111";
        private const string Email = "owner@example.com";
        private const string DeviceId = "22222222-2222-2222-2222-222222222222";
        private const int Iterations = 100_000;
        private readonly CryptoService _crypto = new();

        public Fixture(
            bool? hasMasterPassword,
            bool? claimedByOrganization = false,
            IReadOnlyList<VaultOrganization>? organizations = null,
            IAccountDeletionCoordinationGate? coordinationGate = null,
            TimeSpan? coordinationWaitTimeout = null)
        {
            Endpoints = ServerEndpoints.ForSelfHosted("https://vault.example.test");
            Session = new VaultSession();
            Lease = Session.BeginAccountSession(AccountId, Email, Endpoints);
            Persisted = new PersistedSession(
                PersistedSession.CurrentSchemaVersion,
                AccountId,
                Endpoints,
                Lease.Generation,
                Email,
                DeviceId,
                "refresh-token",
                "protected-user-key",
                KdfType.Pbkdf2,
                Iterations,
                null,
                null,
                Email) with
            {
                HasMasterPassword = hasMasterPassword,
            };

            TokenStore = new RecordingTokenStore();
            TokenStore.Save(Persisted);
            CacheStore = new MultiAccountCacheStore();
            CacheStore.Seed(Persisted);
            DeviceKeyStore = new MemoryDeviceKeyStore();
            DeviceBinding = new DeviceKeyBinding(AccountId, Endpoints, DeviceId);
            DeviceKeyStore.Save(DeviceBinding, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
            CipherStore = new EmptyCipherCreateStore();
            ApprovalStore = new EmptyApprovalStore();
            TdeStore = new EmptyTdeStore();
            FaviconCache = new EmptyFaviconCache();
            Tombstones = new MemoryAccountDeletionTombstoneStore(Events);
            Api = new FakeAccountApiClient();

            var userKey = new SymmetricCryptoKey(
                Enumerable.Range(1, 64).Select(i => (byte)i).ToArray());
            Session.TryCommit(Lease, () =>
            {
                Session.SetTokens("access-token", "refresh-token");
                Session.SetUnlockedKey(userKey);
                Session.SetSnapshot(new DecryptedVault(
                    new AccountInfo(AccountId, Email, Endpoints, "O", "PBKDF2")
                    {
                        UserIsClaimedByOrganization = claimedByOrganization,
                    },
                    [],
                    [],
                    0,
                    0)
                {
                    Organizations = organizations ?? Array.Empty<VaultOrganization>(),
                });
            });

            Binding = new AccountDeletionBinding(AccountId, Email, Endpoints, DeviceId);
            Service = new AccountDeletionService(
                _crypto,
                FakeVaultApiClientProvider.ForAuthenticated<IAccountApiClient>(Api),
                Session,
                TokenStore,
                CacheStore,
                DeviceKeyStore,
                CipherStore,
                ApprovalStore,
                TdeStore,
                FaviconCache,
                Tombstones,
                TimeProvider.System,
                coordinationGate,
                coordinationWaitTimeout);
        }

        public List<string> Events { get; } = [];
        public ServerEndpoints Endpoints { get; }
        public VaultSession Session { get; }
        public VaultSessionLease Lease { get; }
        public PersistedSession Persisted { get; }
        public RecordingTokenStore TokenStore { get; }
        public MultiAccountCacheStore CacheStore { get; }
        public MemoryDeviceKeyStore DeviceKeyStore { get; }
        public EmptyCipherCreateStore CipherStore { get; }
        public EmptyApprovalStore ApprovalStore { get; }
        public EmptyTdeStore TdeStore { get; }
        public EmptyFaviconCache FaviconCache { get; }
        public MemoryAccountDeletionTombstoneStore Tombstones { get; }
        public FakeAccountApiClient Api { get; }
        public AccountDeletionService Service { get; }
        public AccountDeletionBinding Binding { get; }
        public DeviceKeyBinding DeviceBinding { get; }

        public string ExpectedMasterPasswordHash()
        {
            var masterKey = _crypto.DeriveMasterKey(
                MasterPassword,
                Email,
                KdfType.Pbkdf2,
                Iterations,
                null,
                null);
            try
            {
                return _crypto.ComputeMasterPasswordHash(masterKey, MasterPassword);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }

        public AccountDeletionTombstoneRecord ReadTombstone() =>
            Tombstones.Read(Binding).Record
            ?? throw new InvalidOperationException("The expected tombstone is absent.");

        public OtherAccount AddOtherAccount()
        {
            const string accountId = "33333333-3333-3333-3333-333333333333";
            const string deviceId = "44444444-4444-4444-4444-444444444444";
            var persisted = Persisted with
            {
                AccountId = accountId,
                Email = "other@example.com",
                DeviceIdentifier = deviceId,
                Generation = Persisted.Generation + 10,
            };
            TokenStore.Save(persisted);
            TokenStore.Save(Persisted);
            CacheStore.Seed(persisted);
            var binding = new DeviceKeyBinding(accountId, Endpoints, deviceId);
            DeviceKeyStore.Save(binding, Enumerable.Repeat((byte)7, 64).ToArray());
            return new OtherAccount(persisted, binding);
        }

        public void Dispose()
        {
            try
            {
                Session.Logout();
            }
            catch
            {
                // Tests dispose best-effort state after assertions.
            }
            DeviceKeyStore.Dispose();
        }
    }

    private sealed record OtherAccount(
        PersistedSession Session,
        DeviceKeyBinding DeviceBinding);

    private sealed class RecordingTokenStore : ITokenStore
    {
        private readonly MemoryTokenStore _inner = new();

        public bool ThrowOnRemove { get; set; }
        public List<(string AccountId, ServerEndpoints Endpoints)> RemovedBindings { get; } = [];

        public bool TryLoad(out PersistedSession session) => _inner.TryLoad(out session);
        public void Save(PersistedSession session) => _inner.Save(session);
        public void Clear() => _inner.Clear();
        public IReadOnlyList<PersistedAccountMetadata> GetSavedAccounts() =>
            _inner.GetSavedAccounts();
        public bool TryActivate(
            string accountId,
            ServerEndpoints endpoints,
            out PersistedSession session) =>
            _inner.TryActivate(accountId, endpoints, out session);

        public bool Remove(string accountId, ServerEndpoints endpoints)
        {
            RemovedBindings.Add((accountId, endpoints));
            if (ThrowOnRemove)
                throw new IOException("Injected token removal failure.");
            return _inner.Remove(accountId, endpoints);
        }

        public bool Contains(PersistedSession expected) =>
            _inner.GetSavedAccounts().Any(candidate =>
                candidate.AccountId == expected.AccountId
                && candidate.Endpoints == expected.Endpoints);
    }

    private sealed class MultiAccountCacheStore : ICacheStore
    {
        private readonly Dictionary<(string AccountId, ServerEndpoints Endpoints), VaultCacheEntry>
            _entries = [];

        public bool ThrowOnRemove { get; set; }
        public VaultCacheRemovalStatus? RemovalOverride { get; set; }
        public List<(string AccountId, ServerEndpoints Endpoints)> RemovedBindings { get; } = [];

        public void Seed(PersistedSession session) => SaveVaultCache(new VaultCacheEntry(
            VaultCacheEntry.CurrentSchemaVersion,
            session.AccountId,
            session.Endpoints!,
            session.Generation,
            session.Email,
            DateTimeOffset.UtcNow,
            "{}"));

        public void SaveVaultCache(VaultCacheEntry entry) =>
            _entries[(entry.AccountId, entry.Endpoints!)] = entry;

        public bool TryLoadVaultCache(out VaultCacheEntry entry)
        {
            entry = _entries.Values.FirstOrDefault()!;
            return entry is not null;
        }

        public bool TryLoadVaultCache(
            string accountId,
            ServerEndpoints endpoints,
            out VaultCacheEntry entry)
        {
            var found = _entries.TryGetValue((accountId, endpoints), out var stored);
            entry = stored!;
            return found;
        }

        public void ClearVaultCache() => _entries.Clear();

        public void ClearVaultCache(string accountId, ServerEndpoints endpoints)
        {
            RemovedBindings.Add((accountId, endpoints));
            if (ThrowOnRemove)
                throw new IOException("Injected cache removal failure.");
            _entries.Remove((accountId, endpoints));
        }

        public VaultCacheRemovalStatus RemoveVaultCacheExact(
            string accountId,
            ServerEndpoints endpoints)
        {
            RemovedBindings.Add((accountId, endpoints));
            if (ThrowOnRemove)
                return VaultCacheRemovalStatus.Unavailable;
            if (RemovalOverride is { } status)
                return status;
            return _entries.Remove((accountId, endpoints))
                ? VaultCacheRemovalStatus.Removed
                : VaultCacheRemovalStatus.Absent;
        }

        public bool Contains(string accountId, ServerEndpoints endpoints) =>
            _entries.ContainsKey((accountId, endpoints));
    }

    private sealed class MemoryDeviceKeyStore : IDeviceKeyStore, IDisposable
    {
        private readonly Dictionary<DeviceKeyBinding, byte[]> _keys = [];

        public DeviceKeyRemovalStatus? RemovalOverride { get; set; }
        public List<DeviceKeyBinding> RemovedBindings { get; } = [];

        public bool TryLoad(DeviceKeyBinding binding, out byte[] deviceKey)
        {
            if (_keys.TryGetValue(binding, out var stored))
            {
                deviceKey = stored.ToArray();
                return true;
            }
            deviceKey = [];
            return false;
        }

        public void Save(DeviceKeyBinding binding, ReadOnlySpan<byte> deviceKey) =>
            _keys[binding] = deviceKey.ToArray();

        public bool Remove(DeviceKeyBinding binding) =>
            RemoveExact(binding) == DeviceKeyRemovalStatus.Removed;

        public DeviceKeyRemovalStatus RemoveExact(DeviceKeyBinding binding)
        {
            RemovedBindings.Add(binding);
            if (RemovalOverride is { } status)
                return status;
            if (!_keys.Remove(binding, out var removed))
                return DeviceKeyRemovalStatus.Absent;
            CryptographicOperations.ZeroMemory(removed);
            return DeviceKeyRemovalStatus.Removed;
        }

        public AccountScopedRemovalStatus RemoveAccountScope(
            string accountId,
            ServerEndpoints endpoints)
        {
            var matches = _keys.Keys.Where(binding =>
                binding.AccountId == accountId
                && binding.Endpoints == endpoints).ToArray();
            if (matches.Length == 0)
                return AccountScopedRemovalStatus.Absent;
            foreach (var binding in matches)
            {
                RemovedBindings.Add(binding);
                if (_keys.Remove(binding, out var removed))
                    CryptographicOperations.ZeroMemory(removed);
            }
            return AccountScopedRemovalStatus.Removed;
        }

        public bool Contains(DeviceKeyBinding binding) => _keys.ContainsKey(binding);

        public void Dispose()
        {
            foreach (var key in _keys.Values)
                CryptographicOperations.ZeroMemory(key);
            _keys.Clear();
        }
    }

    private sealed class EmptyCipherCreateStore : ICipherCreatePendingStore
    {
        public List<(string AccountId, ServerEndpoints Endpoints)> ReadBindings { get; } = [];
        public CipherCreatePendingReadStatus? StatusOverride { get; set; }

        public CipherCreatePendingReadResult Read(
            string accountId,
            ServerEndpoints endpoints)
        {
            ReadBindings.Add((accountId, endpoints));
            return StatusOverride switch
            {
                CipherCreatePendingReadStatus.Corrupt => CipherCreatePendingReadResult.Corrupt,
                CipherCreatePendingReadStatus.Unavailable => CipherCreatePendingReadResult.Unavailable,
                _ => CipherCreatePendingReadResult.Absent,
            };
        }

        public ICipherCreatePendingReservation? TryCreate(CipherCreatePending pending) =>
            throw new NotSupportedException();
        public CipherCreatePendingRecord? TryUpdate(
            CipherCreatePendingRecord expected,
            CipherCreatePending pending) => throw new NotSupportedException();
        public CipherCreatePendingRecord? Release(
            CipherCreatePendingRecord expected,
            CipherCreatePending pending) => throw new NotSupportedException();
        public bool RemoveConfirmed(CipherCreatePendingRecord expected) =>
            throw new NotSupportedException();
        public bool Abandon(CipherCreatePendingRecord expected) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyApprovalStore : ITrustedDeviceApprovalStore
    {
        public TrustedDeviceApprovalStoreReadStatus? StatusOverride { get; set; }
        public DeviceKeyBinding? LastReadBinding { get; private set; }
        public (string AccountId, ServerEndpoints Endpoints)? LastPurgedScope { get; private set; }

        public TrustedDeviceApprovalStoreReadResult Read(DeviceKeyBinding binding)
        {
            LastReadBinding = binding;
            return StatusOverride switch
            {
                TrustedDeviceApprovalStoreReadStatus.Corrupt =>
                    TrustedDeviceApprovalStoreReadResult.Corrupt,
                TrustedDeviceApprovalStoreReadStatus.Unavailable =>
                    TrustedDeviceApprovalStoreReadResult.Unavailable,
                _ => TrustedDeviceApprovalStoreReadResult.Absent,
            };
        }

        public TrustedDeviceApprovalStoreReadResult Read(
            DeviceKeyBinding binding,
            string requestId) => Read(binding);
        public ITrustedDeviceApprovalIntentLease? TryAcquireBeginIntent(
            DeviceKeyBinding binding) => throw new NotSupportedException();
        public TrustedDeviceApprovalStoreRecord? TryCreate(
            ITrustedDeviceApprovalIntentLease intent,
            TrustedDeviceApprovalPending pending) => throw new NotSupportedException();
        public bool Remove(
            TrustedDeviceApprovalStoreRecord expected,
            TrustedDeviceApprovalRemovalReason reason) => throw new NotSupportedException();

        public AccountScopedRemovalStatus RemoveAccountScope(
            string accountId,
            ServerEndpoints endpoints)
        {
            LastPurgedScope = (accountId, endpoints);
            return StatusOverride switch
            {
                TrustedDeviceApprovalStoreReadStatus.Corrupt =>
                    AccountScopedRemovalStatus.Corrupt,
                TrustedDeviceApprovalStoreReadStatus.Unavailable =>
                    AccountScopedRemovalStatus.Unavailable,
                _ => AccountScopedRemovalStatus.Absent,
            };
        }
    }

    private sealed class EmptyTdeStore : ITdeV2JitPendingStore
    {
        public TdeV2JitPendingReadStatus? StatusOverride { get; set; }
        public DeviceKeyBinding? LastReadBinding { get; private set; }
        public (string AccountId, ServerEndpoints Endpoints)? LastPurgedScope { get; private set; }

        public TdeV2JitPendingReadResult Read(DeviceKeyBinding binding)
        {
            LastReadBinding = binding;
            return StatusOverride switch
            {
                TdeV2JitPendingReadStatus.Corrupt => TdeV2JitPendingReadResult.Corrupt,
                TdeV2JitPendingReadStatus.Unavailable => TdeV2JitPendingReadResult.Unavailable,
                _ => TdeV2JitPendingReadResult.Absent,
            };
        }

        public TdeV2JitPendingStoreRecord? TryCreate(TdeV2JitPending pending) =>
            throw new NotSupportedException();
        public TdeV2JitPendingStoreRecord? TryAdvance(
            TdeV2JitPendingStoreRecord expected,
            TdeV2JitPendingStage nextStage) => throw new NotSupportedException();
        public bool Remove(
            TdeV2JitPendingStoreRecord expected,
            TdeV2JitPendingRemovalReason reason) => throw new NotSupportedException();

        public AccountScopedRemovalStatus RemoveAccountScope(
            string accountId,
            ServerEndpoints endpoints)
        {
            LastPurgedScope = (accountId, endpoints);
            return StatusOverride switch
            {
                TdeV2JitPendingReadStatus.Corrupt => AccountScopedRemovalStatus.Corrupt,
                TdeV2JitPendingReadStatus.Unavailable => AccountScopedRemovalStatus.Unavailable,
                _ => AccountScopedRemovalStatus.Absent,
            };
        }
    }

    private sealed class EmptyFaviconCache : IFaviconCache
    {
        public FaviconCacheClearStatus Status { get; set; } =
            FaviconCacheClearStatus.Absent;
        public int AccountDeletionClearCount { get; private set; }

        public Task<byte[]?> GetAsync(string domain, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);

        public void Clear()
        {
        }

        public FaviconCacheClearStatus ClearForAccountDeletion()
        {
            AccountDeletionClearCount++;
            return Status;
        }
    }

    private sealed class MemoryAccountDeletionTombstoneStore(
        List<string> events) : IAccountDeletionTombstoneStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<AccountDeletionBinding, AccountDeletionTombstoneRecord>
            _records = [];

        public Action? OnCreate { get; set; }
        public bool FailAdvance { get; set; }
        public bool FailRemove { get; set; }
        public AccountDeletionTombstoneRemovalReason? LastRemovalReason { get; private set; }

        public AccountDeletionTombstoneReadResult Read(AccountDeletionBinding binding)
        {
            lock (_gate)
            {
                return _records.TryGetValue(binding, out var record)
                    ? AccountDeletionTombstoneReadResult.Found(record)
                    : AccountDeletionTombstoneReadResult.Absent;
            }
        }

        public AccountDeletionTombstoneReadAllResult ReadAll()
        {
            lock (_gate)
            {
                return _records.Count == 0
                    ? AccountDeletionTombstoneReadAllResult.Absent
                    : AccountDeletionTombstoneReadAllResult.Found(
                        _records.Values.ToArray());
            }
        }

        public AccountDeletionTombstoneRecord? TryCreate(
            AccountDeletionBinding binding,
            DateTimeOffset createdAt)
        {
            AccountDeletionTombstoneRecord record;
            lock (_gate)
            {
                if (_records.Keys.Any(candidate =>
                    candidate.AccountId == binding.AccountId
                    && candidate.Endpoints == binding.Endpoints))
                {
                    return null;
                }
                record = new AccountDeletionTombstoneRecord(
                    Guid.NewGuid(),
                    1,
                    createdAt,
                    createdAt,
                    binding,
                    AccountDeletionStage.Prepared);
                _records.Add(binding, record);
                events.Add("tombstone:prepared");
            }
            OnCreate?.Invoke();
            return record;
        }

        public AccountDeletionTombstoneRecord? TryAdvance(
            AccountDeletionTombstoneRecord expected,
            AccountDeletionStage nextStage,
            DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                var validTransition = expected.Stage == AccountDeletionStage.Prepared
                        && nextStage == AccountDeletionStage.ServerConfirmed
                    || expected.Stage == AccountDeletionStage.Prepared
                        && nextStage == AccountDeletionStage.UncertainLocalCleanupComplete
                    || expected.Stage == AccountDeletionStage.ServerConfirmed
                        && nextStage == AccountDeletionStage.LocalCleanupComplete;
                if (FailAdvance
                    || !validTransition
                    || !_records.TryGetValue(expected.Binding, out var current)
                    || current != expected
                    || current.Stage != expected.Stage)
                {
                    return null;
                }

                var advanced = new AccountDeletionTombstoneRecord(
                    current.EntryId,
                    checked(current.Revision + 1),
                    current.CreatedAt,
                    updatedAt,
                    current.Binding,
                    nextStage);
                _records[current.Binding] = advanced;
                events.Add(nextStage == AccountDeletionStage.ServerConfirmed
                    ? "tombstone:server-confirmed"
                    : "tombstone:local-cleanup-complete");
                return advanced;
            }
        }

        public bool Remove(
            AccountDeletionTombstoneRecord expected,
            AccountDeletionTombstoneRemovalReason reason)
        {
            lock (_gate)
            {
                if (FailRemove
                    || !_records.TryGetValue(expected.Binding, out var current)
                    || current != expected
                    || !ReasonMatchesStage(reason, current.Stage))
                {
                    return false;
                }

                _records.Remove(expected.Binding);
                LastRemovalReason = reason;
                events.Add($"tombstone:remove:{reason}");
                return true;
            }
        }

        private static bool ReasonMatchesStage(
            AccountDeletionTombstoneRemovalReason reason,
            AccountDeletionStage stage) => reason switch
            {
                AccountDeletionTombstoneRemovalReason.ServerRejectedPreparedRequest
                    =>
                    stage == AccountDeletionStage.Prepared,
                AccountDeletionTombstoneRemovalReason.CancelledBeforeDispatch =>
                    stage == AccountDeletionStage.Prepared,
                _ => false,
            };
    }
}
