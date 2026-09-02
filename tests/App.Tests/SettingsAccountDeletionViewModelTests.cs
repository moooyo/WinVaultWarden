using App.Services;
using App.ViewModels;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public sealed class SettingsAccountDeletionViewModelTests : AppPreferencesTestBase
{
    [Fact]
    public void GetPreflight_ReturnsFacadeSnapshot()
    {
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service);

        var result = viewModel.GetAccountDeletionPreflight();

        Assert.True(result.IsSuccess);
        Assert.Same(service.Preflight, result.Value);
    }

    [Fact]
    public async Task RequestOtp_PreservesDomainOutcome()
    {
        var service = new FakeAccountDeletionService();
        service.OtpResult = new AccountDeletionOtpRequestResult.Sent(service.Preflight);
        var viewModel = new SettingsViewModel(service);

        var result = await viewModel.RequestAccountDeletionOtpAsync();

        Assert.True(result.IsSuccess);
        Assert.IsType<AccountDeletionOtpRequestResult.Sent>(result.Value);
        Assert.Equal(1, service.RequestOtpCalls);
    }

    [Fact]
    public async Task DeleteAccount_PassesOwnedCredentialAndPreservesRejectedResult()
    {
        var service = new FakeAccountDeletionService
        {
            DeleteResult = new AccountDeletionResult.Rejected(
                AccountDeletionRejectionReason.InvalidCredential),
        };
        var viewModel = new SettingsViewModel(service);
        var credential = AccountDeletionCredential.FromMasterPassword("password".AsSpan());

        var result = await viewModel.DeleteAccountAsync(credential);

        Assert.True(result.IsSuccess);
        Assert.IsType<AccountDeletionResult.Rejected>(result.Value);
        Assert.Equal(AccountDeletionCredentialKind.MasterPassword, service.LastCredentialKind);
    }

    [Fact]
    public async Task Confirmed_TransitionsWithLockOnly()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.Confirmed(service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task OutcomeUncertain_TransitionsWithLockOnly()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.OutcomeUncertain(
            AccountDeletionUncertainReason.TransportFailure,
            service.Pending,
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task OutcomeUncertainLocalCleanupComplete_TransitionsWithLockOnly()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.OutcomeUncertainLocalCleanupComplete(
            service.Pending,
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task OutcomeUncertainLocalCleanupIncomplete_TransitionsWithLockOnly()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete(
            service.Pending,
            [new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.VaultCache,
                AccountDeletionCleanupFailureKind.Failed)],
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task ConfirmedCleanupIncomplete_TransitionsWithLockOnly()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
            service.ConfirmedPending,
            [new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.VaultCache,
                AccountDeletionCleanupFailureKind.Failed)],
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task Rejected_DoesNotRequestLifecycleTransition()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.Rejected(
            AccountDeletionRejectionReason.ServerRejected,
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Empty(lifecycle.Requests);
    }

    [Fact]
    public async Task RejectedWithRetainedPending_TransitionsWithLock()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.Rejected(
            AccountDeletionRejectionReason.ServerRejected,
            service.Preflight,
            Pending: service.Pending);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task CancelledWithRetainedPending_TransitionsWithLock()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var viewModel = new SettingsViewModel(service, lifecycle);
        var result = new AccountDeletionResult.CancelledBeforeDispatch(
            service.Preflight,
            service.Pending);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task LateResult_WhenDifferentAccountIsCurrent_SkipsGlobalLifecycle()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var accountB = CreateAccount(
            "account-b",
            ServerEndpoints.ForSelfHosted("https://vault.example.com"));
        var vault = new SequenceVaultService(accountB);
        var viewModel = new SettingsViewModel(service, lifecycle, vault);
        var result = new AccountDeletionResult.Confirmed(service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Empty(lifecycle.Requests);
    }

    [Fact]
    public async Task LateResult_WhenSameIdUsesDifferentEndpoints_SkipsGlobalLifecycle()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var accountB = CreateAccount(
            service.Preflight.Binding.AccountId,
            ServerEndpoints.ForSelfHosted("https://other.example.com"));
        var vault = new SequenceVaultService(accountB);
        var viewModel = new SettingsViewModel(service, lifecycle, vault);
        var result = new AccountDeletionResult.OutcomeUncertain(
            AccountDeletionUncertainReason.TransportFailure,
            service.Pending,
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Empty(lifecycle.Requests);
    }

    [Fact]
    public async Task UncertainCleanupComplete_WhenAccountBIsCurrent_SkipsGlobalLifecycle()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var accountB = CreateAccount(
            "account-b",
            ServerEndpoints.ForSelfHosted("https://other.example.com"));
        var vault = new SequenceVaultService(accountB);
        var viewModel = new SettingsViewModel(service, lifecycle, vault);
        var result = new AccountDeletionResult.OutcomeUncertainLocalCleanupComplete(
            service.Pending,
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Empty(lifecycle.Requests);
    }

    [Fact]
    public async Task UncertainCleanupIncomplete_WhenAccountBIsCurrent_SkipsGlobalLifecycle()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var accountB = CreateAccount(
            "account-b",
            ServerEndpoints.ForSelfHosted("https://other.example.com"));
        var vault = new SequenceVaultService(accountB);
        var viewModel = new SettingsViewModel(service, lifecycle, vault);
        var result = new AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete(
            service.Pending,
            [new AccountDeletionCleanupFailure(
                AccountDeletionCleanupTarget.Token,
                AccountDeletionCleanupFailureKind.Failed)],
            service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Empty(lifecycle.Requests);
    }

    [Fact]
    public async Task LateResult_WhenDeletedAccountIsStillCurrent_Transitions()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var accountA = CreateAccount(
            service.Preflight.Binding.AccountId,
            service.Preflight.Binding.Endpoints);
        var vault = new SequenceVaultService(accountA);
        var viewModel = new SettingsViewModel(service, lifecycle, vault);
        var result = new AccountDeletionResult.Confirmed(service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(1, vault.SnapshotReads);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task LateResult_WhenCurrentAccountIsEmpty_Transitions()
    {
        var lifecycle = new RecordingLifecycle();
        var service = new FakeAccountDeletionService();
        var vault = new SequenceVaultService(AccountInfo.Empty);
        var viewModel = new SettingsViewModel(service, lifecycle, vault);
        var result = new AccountDeletionResult.Confirmed(service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(1, vault.SnapshotReads);
        Assert.Equal(new[] { AppLifecycleIntent.Lock }, lifecycle.Requests);
    }

    [Fact]
    public async Task ConditionalLifecycle_WhenAccountBActivatesBeforePredicate_SkipsGlobalLifecycle()
    {
        var service = new FakeAccountDeletionService();
        var accountA = CreateAccount(
            service.Preflight.Binding.AccountId,
            service.Preflight.Binding.Endpoints);
        var accountB = CreateAccount(
            "account-b",
            ServerEndpoints.ForSelfHosted("https://other.example.com"));
        var vault = new SequenceVaultService(accountA, accountB);
        var lifecycle = new RecordingLifecycle
        {
            BeforeConditionalPredicate = vault.Advance,
        };
        var viewModel = new SettingsViewModel(service, lifecycle, vault);
        var result = new AccountDeletionResult.Confirmed(service.Preflight);

        var transition = await viewModel.TransitionAfterAccountDeletionAsync(result);

        Assert.True(transition.IsSuccess);
        Assert.Equal(2, vault.SnapshotReads);
        Assert.Equal(1, lifecycle.ConditionalRequests);
        Assert.Empty(lifecycle.Requests);
    }

    private static AccountInfo CreateAccount(string accountId, ServerEndpoints endpoints) =>
        new(accountId, "user@example.com", endpoints, "U", string.Empty);

    private sealed class FakeAccountDeletionService : IAccountDeletionService
    {
        public FakeAccountDeletionService()
        {
            var binding = new AccountDeletionBinding(
                "account-id",
                "user@example.com",
                ServerEndpoints.ForSelfHosted("https://vault.example.com"),
                "11111111-1111-1111-1111-111111111111");
            Preflight = new AccountDeletionPreflight(
                binding,
                AccountDeletionCredentialRequirement.MasterPassword,
                isClaimedByOrganization: false,
                ownerWarnings: [],
                blockReason: AccountDeletionPreflightBlockReason.None);
            var now = DateTimeOffset.UtcNow;
            Pending = new AccountDeletionTombstoneRecord(
                entryId: Guid.NewGuid(),
                revision: 1,
                createdAt: now,
                updatedAt: now,
                binding: binding,
                stage: AccountDeletionStage.Prepared);
            ConfirmedPending = new AccountDeletionTombstoneRecord(
                entryId: Guid.NewGuid(),
                revision: 2,
                createdAt: now,
                updatedAt: now.AddSeconds(1),
                binding: binding,
                stage: AccountDeletionStage.ServerConfirmed);
            OtpResult = new AccountDeletionOtpRequestResult.Rejected(
                AccountDeletionRejectionReason.WrongCredentialKind,
                Preflight);
            DeleteResult = new AccountDeletionResult.Confirmed(Preflight);
        }

        public AccountDeletionPreflight Preflight { get; set; }
        public AccountDeletionOtpRequestResult OtpResult { get; set; }
        public AccountDeletionResult DeleteResult { get; set; }
        public AccountDeletionTombstoneRecord Pending { get; }
        public AccountDeletionTombstoneRecord ConfirmedPending { get; }
        public int RequestOtpCalls { get; private set; }
        public AccountDeletionCredentialKind? LastCredentialKind { get; private set; }

        public AccountDeletionPreflight GetPreflight() => Preflight;

        public Task<AccountDeletionOtpRequestResult> RequestOtpAsync(
            CancellationToken ct = default)
        {
            RequestOtpCalls++;
            return Task.FromResult(OtpResult);
        }

        public Task<AccountDeletionResult> DeleteAsync(
            AccountDeletionCredential credential,
            CancellationToken ct = default)
        {
            LastCredentialKind = credential.Kind;
            return Task.FromResult(DeleteResult);
        }

        public AccountDeletionTombstoneReadResult ReadPending(
            AccountDeletionBinding binding) => AccountDeletionTombstoneReadResult.Absent;

        public AccountDeletionTombstoneReadAllResult ReadPending() =>
            AccountDeletionTombstoneReadAllResult.Absent;

        public Task<AccountDeletionResult> ResumeConfirmedCleanupAsync(
            AccountDeletionTombstoneRecord expected,
            CancellationToken ct = default) => Task.FromResult(DeleteResult);

        public Task<AccountDeletionLocalResolutionResult> ClearLocalDataForPendingAsync(
            AccountDeletionTombstoneRecord expected,
            CancellationToken ct = default)
            => Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.Completed));

    }

    private sealed class RecordingLifecycle : IAppLifecycleCoordinator
    {
        public List<AppLifecycleIntent> Requests { get; } = [];
        public bool IsTransitioning => false;
        public Action? BeforeConditionalPredicate { get; init; }
        public int ConditionalRequests { get; private set; }

        public Task RequestAsync(
            AppLifecycleIntent intent,
            VaultSessionLease? expectedSession = null)
        {
            Requests.Add(intent);
            return Task.CompletedTask;
        }

        public async Task<bool> RequestIfAsync(
            AppLifecycleIntent intent,
            Func<bool> predicate)
        {
            ConditionalRequests++;
            BeforeConditionalPredicate?.Invoke();
            bool accepted;
            try
            {
                accepted = predicate();
            }
            catch
            {
                return false;
            }
            if (!accepted)
                return false;

            await RequestAsync(intent);
            return true;
        }

        public bool MarkSessionReady() => true;
        public void FailClosedSynchronously() { }
    }

    private sealed class SequenceVaultService(params AccountInfo[] accounts) : IVaultService
    {
        private int _snapshotReads;

        public int SnapshotReads => Volatile.Read(ref _snapshotReads);

        public void Advance() => Interlocked.Increment(ref _snapshotReads);

        public IVaultSnapshot Snapshot
        {
            get
            {
                var read = Interlocked.Increment(ref _snapshotReads) - 1;
                var index = Math.Min(read, accounts.Length - 1);
                return new AccountSnapshot(accounts[index]);
            }
        }

        public IReadOnlyList<Cipher> GetCiphers() => [];
        public IReadOnlyList<Folder> GetFolders() => [];
        public IReadOnlyList<DeviceInfo> GetDevices() => [];
        public VaultSessionLease CaptureLease() => throw new NotSupportedException();
        public bool IsCurrent(VaultSessionLease lease) => false;
        public void ThrowIfExpired(VaultSessionLease lease) => throw new NotSupportedException();
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) => [];
    }

    private sealed record AccountSnapshot(AccountInfo Account) : IVaultSnapshot
    {
        public VaultState State => VaultState.Unlocked;
        public IReadOnlyList<Cipher> Ciphers => [];
        public IReadOnlyList<Folder> Folders => [];
        public IReadOnlyList<DeviceInfo> Devices => [];
        public int SkippedCipherCount => 0;
        public int SkippedFolderCount => 0;
        public bool DataStale => false;
        public long Generation => 1;
    }
}
