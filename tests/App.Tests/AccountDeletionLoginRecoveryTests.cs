using System.Xml.Linq;
using App.Services;
using App.ViewModels;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class AccountDeletionLoginRecoveryTests
{
    [Fact]
    public void PreparedTombstone_BlocksEverySavedSessionUnlockPath()
    {
        var session = Session();
        var pending = Pending(session, AccountDeletionStage.Prepared);
        var deletion = DeletionWith(pending);
        var vm = CreateViewModel(deletion);

        vm.PreparePersistedSession(session);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.Prepared,
            vm.AccountDeletionRecoveryNotice);
        Assert.True(vm.ShowClearPendingAccountDeletion);
        Assert.False(vm.ShowRetryConfirmedAccountDeletionCleanup);
        Assert.False(vm.ShowFullLocalResetAction);
        Assert.False(vm.ShowUnlockPanel);
        Assert.False(vm.IsPinUnlockStage);
        Assert.False(vm.ShowHelloUnlock);
        Assert.False(vm.ShowPrimaryAction);
        Assert.Equal(
            LoginFocusTarget.AccountDeletionRecoveryAction,
            vm.FocusTarget);
    }

    [Fact]
    public void ServerConfirmedTombstone_OffersOnlyCleanupResume()
    {
        var session = Session();
        var pending = Pending(session, AccountDeletionStage.ServerConfirmed);
        var vm = CreateViewModel(DeletionWith(pending));

        vm.PreparePersistedSession(session);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.ServerConfirmed,
            vm.AccountDeletionRecoveryNotice);
        Assert.False(vm.ShowClearPendingAccountDeletion);
        Assert.True(vm.ShowRetryConfirmedAccountDeletionCleanup);
        Assert.False(vm.ShowFullLocalResetAction);
        Assert.Contains("服务器已确认", vm.AccountDeletionRecoveryMessage);
        Assert.Equal("使用其他账户", vm.AccountDeletionRecoveryContinueText);
    }

    [Fact]
    public void CompletedMarker_WithMatchingActiveSession_BlocksStaleUnlockAndOffersRepurge()
    {
        var session = Session();
        var complete = Pending(session, AccountDeletionStage.LocalCleanupComplete);
        var vm = CreateViewModel(DeletionWith(complete));

        vm.PreparePersistedSession(session);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.LocalCleanupComplete,
            vm.AccountDeletionRecoveryNotice);
        Assert.False(vm.ShowClearPendingAccountDeletion);
        Assert.True(vm.ShowRetryConfirmedAccountDeletionCleanup);
        Assert.False(vm.ShowUnlockPanel);
        Assert.Contains("永久安全记录", vm.AccountDeletionRecoveryMessage);
        Assert.Contains("旧进程", vm.AccountDeletionRecoveryMessage);
        Assert.Equal("使用其他账户", vm.AccountDeletionRecoveryContinueText);
    }

    [Fact]
    public void UncertainCompletedMarker_WithMatchingActiveSession_UsesDedicatedStaleNotice()
    {
        var session = Session();
        var complete = Pending(
            session,
            AccountDeletionStage.UncertainLocalCleanupComplete);
        var vm = CreateViewModel(DeletionWith(complete));

        vm.PreparePersistedSession(session);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete,
            vm.AccountDeletionRecoveryNotice);
        Assert.True(vm.ShowRetryConfirmedAccountDeletionCleanup);
        Assert.False(vm.ShowUnlockPanel);
        Assert.Contains("删除结果不确定", vm.AccountDeletionRecoveryMessage);
        Assert.Contains("旧进程", vm.AccountDeletionRecoveryMessage);
    }

    [Theory]
    [InlineData(AccountDeletionTombstoneReadStatus.Corrupt)]
    [InlineData(AccountDeletionTombstoneReadStatus.Unavailable)]
    public void UnsafeLedgerState_FailsClosedInsteadOfPreparingUnlock(
        AccountDeletionTombstoneReadStatus status)
    {
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = status == AccountDeletionTombstoneReadStatus.Corrupt
                ? AccountDeletionTombstoneReadAllResult.Corrupt
                : AccountDeletionTombstoneReadAllResult.Unavailable,
        };
        var vm = CreateViewModel(deletion);

        vm.PreparePersistedSession(Session());

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.LedgerUnavailable,
            vm.AccountDeletionRecoveryNotice);
        Assert.False(vm.ShowUnlockPanel);
        Assert.False(vm.ShowClearPendingAccountDeletion);
        Assert.False(vm.ShowRetryConfirmedAccountDeletionCleanup);
        Assert.Equal("重新检查安全存储", vm.AccountDeletionRecoveryContinueText);
        Assert.Contains("所有账户登录", vm.AccountDeletionRecoveryMessage);
        Assert.Contains("Windows 应用重置", vm.AccountDeletionRecoveryMessage);
    }

    [Fact]
    public void LedgerUnavailable_AloneShowsAndEnablesFullLocalReset()
    {
        var reset = new ControlledLocalDataResetService();
        var lifecycle = new RecordingLifecycle();
        var vm = CreateViewModel(
            new ControlledAccountDeletionService
            {
                ReadAllResult = AccountDeletionTombstoneReadAllResult.Corrupt,
            },
            localDataReset: reset,
            lifecycle: lifecycle);

        vm.PrepareInitialLoginState(activeSession: null);

        Assert.True(vm.ShowFullLocalResetAction);
        Assert.True(vm.CanRequestFullLocalReset);
        vm.IsBusy = true;
        Assert.False(vm.CanRequestFullLocalReset);
        vm.IsBusy = false;
    }

    [Theory]
    [InlineData(LocalDataResetRequestStatus.Requested)]
    [InlineData(LocalDataResetRequestStatus.AlreadyPending)]
    [InlineData(LocalDataResetRequestStatus.CommitOutcomeUncertain)]
    public async Task FullLocalResetCommitted_RequestsUncancellableExit(
        LocalDataResetRequestStatus status)
    {
        var reset = new ControlledLocalDataResetService
        {
            Result = new LocalDataResetRequestResult(status),
        };
        var lifecycle = new RecordingLifecycle();
        var vm = CreateViewModel(
            new ControlledAccountDeletionService
            {
                ReadAllResult = AccountDeletionTombstoneReadAllResult.Unavailable,
            },
            localDataReset: reset,
            lifecycle: lifecycle);
        vm.PrepareInitialLoginState(activeSession: null);

        await vm.RequestFullLocalResetCommand.ExecuteAsync(null);

        Assert.Equal(1, reset.Requests);
        Assert.Equal([AppLifecycleIntent.Exit], lifecycle.Requests);
        Assert.Contains("安全退出", vm.Status);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanRequestFullLocalReset);
        Assert.False(vm.CanContinueFromAccountDeletionRecovery);
        Assert.False(vm.CanUsePrimaryAction);
    }

    [Fact]
    public async Task FullLocalResetAttempted_LifecycleFailureStaysBusyAndFailsClosed()
    {
        var reset = new ControlledLocalDataResetService
        {
            Result = new LocalDataResetRequestResult(
                LocalDataResetRequestStatus.Requested),
        };
        var lifecycle = new RecordingLifecycle
        {
            RequestFailure = new IOException("exit failed"),
        };
        var vm = CreateViewModel(
            new ControlledAccountDeletionService
            {
                ReadAllResult = AccountDeletionTombstoneReadAllResult.Corrupt,
            },
            localDataReset: reset,
            lifecycle: lifecycle);
        vm.PrepareInitialLoginState(activeSession: null);

        await vm.RequestFullLocalResetCommand.ExecuteAsync(null);

        Assert.Equal([AppLifecycleIntent.Exit], lifecycle.Requests);
        Assert.Equal(1, lifecycle.FailClosedCalls);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanRequestFullLocalReset);
        Assert.False(vm.CanContinueFromAccountDeletionRecovery);
        Assert.False(vm.CanUsePrimaryAction);
        Assert.Contains("请立即关闭应用", vm.Status);
    }

    [Fact]
    public async Task FullLocalResetUncertainCommit_PageDeactivationCannotCancelExit()
    {
        var reset = new ControlledLocalDataResetService
        {
            Result = new LocalDataResetRequestResult(
                LocalDataResetRequestStatus.CommitOutcomeUncertain),
        };
        var lifecycle = new RecordingLifecycle();
        var vm = CreateViewModel(
            new ControlledAccountDeletionService
            {
                ReadAllResult = AccountDeletionTombstoneReadAllResult.Corrupt,
            },
            localDataReset: reset,
            lifecycle: lifecycle);
        reset.OnRequest = vm.Deactivate;
        vm.PrepareInitialLoginState(activeSession: null);

        await vm.RequestFullLocalResetCommand.ExecuteAsync(null);

        Assert.Equal([AppLifecycleIntent.Exit], lifecycle.Requests);
        Assert.Contains("结果无法确认", vm.Status);
        Assert.Contains("下次启动会重新检查", vm.Status);
        Assert.True(vm.IsBusy);
    }

    [Theory]
    [InlineData(LocalDataResetRequestStatus.Corrupt)]
    [InlineData(LocalDataResetRequestStatus.Unavailable)]
    public async Task FullLocalResetUncertainStatus_RequestsExitAndStaysBusy(
        LocalDataResetRequestStatus status)
    {
        var reset = new ControlledLocalDataResetService
        {
            Result = new LocalDataResetRequestResult(status),
        };
        var lifecycle = new RecordingLifecycle();
        var vm = CreateViewModel(
            new ControlledAccountDeletionService
            {
                ReadAllResult = AccountDeletionTombstoneReadAllResult.Corrupt,
            },
            localDataReset: reset,
            lifecycle: lifecycle);
        vm.PrepareInitialLoginState(activeSession: null);

        await vm.RequestFullLocalResetCommand.ExecuteAsync(null);

        Assert.Equal([AppLifecycleIntent.Exit], lifecycle.Requests);
        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Contains("结果无法确认", vm.Status);
        Assert.Contains("下次启动会重新检查", vm.Status);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanRequestFullLocalReset);
        Assert.False(vm.CanUsePrimaryAction);
    }

    [Fact]
    public async Task FullLocalResetServiceException_StillRequestsExitAndFailsClosed()
    {
        var reset = new ControlledLocalDataResetService
        {
            RequestFailure = new IOException("request result lost"),
        };
        var lifecycle = new RecordingLifecycle();
        var vm = CreateViewModel(
            new ControlledAccountDeletionService
            {
                ReadAllResult = AccountDeletionTombstoneReadAllResult.Corrupt,
            },
            localDataReset: reset,
            lifecycle: lifecycle);
        vm.PrepareInitialLoginState(activeSession: null);

        await vm.RequestFullLocalResetCommand.ExecuteAsync(null);

        Assert.Equal([AppLifecycleIntent.Exit], lifecycle.Requests);
        Assert.Equal(1, lifecycle.FailClosedCalls);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanRequestFullLocalReset);
        Assert.Contains("结果无法确认", vm.Status);
    }

    [Fact]
    public void LedgerRetry_RecoversToAccountWhenStoreBecomesAbsent()
    {
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Unavailable,
        };
        var vm = CreateViewModel(deletion);
        vm.PrepareInitialLoginState(activeSession: null);
        deletion.ReadAllResult = AccountDeletionTombstoneReadAllResult.Absent;

        vm.ContinueFromAccountDeletionRecoveryCommand.Execute(null);

        Assert.True(vm.IsAccountStage);
        Assert.Equal(AccountDeletionRecoveryNoticeKind.None, vm.AccountDeletionRecoveryNotice);
        Assert.Contains("已恢复", vm.Status);
    }

    [Fact]
    public void LedgerRetry_TransitionsToExactPendingWhenStoreRecovers()
    {
        var session = Session();
        var pending = Pending(session, AccountDeletionStage.Prepared);
        var deletion = new ControlledAccountDeletionService
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Corrupt,
        };
        var vm = CreateViewModel(deletion);
        vm.PrepareInitialLoginState(activeSession: null);
        deletion.ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
            [pending]);

        vm.ContinueFromAccountDeletionRecoveryCommand.Execute(null);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.Prepared,
            vm.AccountDeletionRecoveryNotice);
        Assert.True(vm.ShowClearPendingAccountDeletion);
    }

    [Fact]
    public void MatchingAccountBindingWithDifferentDevice_RemainsAuthoritative()
    {
        var session = Session();
        var pending = Pending(
            session with { DeviceIdentifier = Guid.NewGuid().ToString("D") },
            AccountDeletionStage.Prepared);
        var vm = CreateViewModel(DeletionWith(pending));

        vm.PreparePersistedSession(session);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.Prepared,
            vm.AccountDeletionRecoveryNotice);
        Assert.True(vm.ShowClearPendingAccountDeletion);
    }

    [Fact]
    public void UnrelatedTombstone_RemainsReachableBeforeUnlockingActiveAccount()
    {
        var session = Session();
        var other = Session("other-account", "https://other.example");
        var vm = CreateViewModel(DeletionWith(Pending(
            other,
            AccountDeletionStage.Prepared)));

        vm.PreparePersistedSession(session);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.Prepared,
            vm.AccountDeletionRecoveryNotice);
    }

    [Theory]
    [InlineData(AccountDeletionStage.Prepared)]
    [InlineData(AccountDeletionStage.ServerConfirmed)]
    public void PendingRecord_RemainsReachableWithoutActiveToken(
        AccountDeletionStage stage)
    {
        var pending = Pending(Session(), stage);
        var vm = CreateViewModel(DeletionWith(pending));

        vm.PrepareInitialLoginState(activeSession: null);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            stage == AccountDeletionStage.Prepared
                ? AccountDeletionRecoveryNoticeKind.Prepared
                : AccountDeletionRecoveryNoticeKind.ServerConfirmed,
            vm.AccountDeletionRecoveryNotice);
    }

    [Theory]
    [InlineData(AccountDeletionStage.LocalCleanupComplete)]
    [InlineData(AccountDeletionStage.UncertainLocalCleanupComplete)]
    public void CompletedMarker_WithoutMatchingActiveSession_DoesNotOccupyRecoveryPage(
        AccountDeletionStage stage)
    {
        var complete = Pending(Session(), stage);
        var vm = CreateViewModel(DeletionWith(complete));

        vm.PrepareInitialLoginState(activeSession: null);

        Assert.True(vm.IsAccountStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.None,
            vm.AccountDeletionRecoveryNotice);
    }

    [Theory]
    [InlineData(AccountDeletionStage.LocalCleanupComplete)]
    [InlineData(AccountDeletionStage.UncertainLocalCleanupComplete)]
    public void CompletedMarker_ForAnotherAccount_DoesNotBlockActiveSessionUnlock(
        AccountDeletionStage stage)
    {
        var active = Session("active-account", "https://active.example");
        var deleted = Session("deleted-account", "https://deleted.example");
        var vm = CreateViewModel(DeletionWith(Pending(
            deleted,
            stage)));

        vm.PrepareInitialLoginState(active);

        Assert.True(vm.IsUnlockStage);
        Assert.False(vm.IsAccountDeletionRecoveryStage);
    }

    [Fact]
    public void ActionableRecords_TakePriorityOverUnmatchedCompletedMarker()
    {
        var complete = Pending(
            Session("complete", "https://complete.example"),
            AccountDeletionStage.LocalCleanupComplete);
        var uncertainComplete = Pending(
            Session("uncertain-complete", "https://uncertain-complete.example"),
            AccountDeletionStage.UncertainLocalCleanupComplete);
        var prepared = Pending(
            Session("prepared", "https://prepared.example"),
            AccountDeletionStage.Prepared);
        var confirmed = Pending(
            Session("confirmed", "https://confirmed.example"),
            AccountDeletionStage.ServerConfirmed);
        var vm = CreateViewModel(DeletionWith(
            complete,
            uncertainComplete,
            prepared,
            confirmed));

        vm.PrepareInitialLoginState(activeSession: null);

        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.ServerConfirmed,
            vm.AccountDeletionRecoveryNotice);
        Assert.Equal(confirmed.Binding.NormalizedEmail, vm.Email);
    }

    [Theory]
    [InlineData(AccountDeletionCleanupTarget.VaultCache)]
    [InlineData(AccountDeletionCleanupTarget.CipherCreatePending)]
    public async Task StartupIncompleteCompletedMarker_WithoutToken_RemainsReachable(
        AccountDeletionCleanupTarget failedTarget)
    {
        var complete = Pending(Session(), AccountDeletionStage.LocalCleanupComplete);
        var prepared = Pending(
            Session("other-account", "https://other.example"),
            AccountDeletionStage.Prepared);
        var deletion = DeletionWith(complete, prepared);
        deletion.ResumeHandler = (record, _) =>
            Task.FromResult<AccountDeletionResult>(
                new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
                    record,
                    [new AccountDeletionCleanupFailure(
                        failedTarget,
                        AccountDeletionCleanupFailureKind.Unavailable)]));
        var recoveryHost = new AccountDeletionRecoveryHost(deletion);
        await recoveryHost.ResumeConfirmedCleanupAsync();
        var vm = CreateViewModel(
            deletion,
            recoveryHost: recoveryHost);

        vm.PrepareInitialLoginState(activeSession: null);

        Assert.Same(complete, Assert.Single(recoveryHost.FailedCleanupRecords));
        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.LocalCleanupComplete,
            vm.AccountDeletionRecoveryNotice);
        Assert.True(vm.ShowRetryConfirmedAccountDeletionCleanup);
        Assert.False(vm.ShowUnlockPanel);
    }

    [Fact]
    public async Task ManualRepurgeSuccess_ResolvesStartupFailureAndStopsPresentingMarker()
    {
        var complete = Pending(Session(), AccountDeletionStage.LocalCleanupComplete);
        var deletion = DeletionWith(complete);
        deletion.ResumeHandler = (record, _) =>
            Task.FromResult<AccountDeletionResult>(
                new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
                    record,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.VaultCache,
                        AccountDeletionCleanupFailureKind.Unavailable)]));
        var recoveryHost = new AccountDeletionRecoveryHost(deletion);
        await recoveryHost.ResumeConfirmedCleanupAsync();
        deletion.ResumeHandler = null;
        var vm = CreateViewModel(
            deletion,
            recoveryHost: recoveryHost);
        vm.PrepareInitialLoginState(activeSession: null);

        await vm.RetryConfirmedAccountDeletionCleanupCommand.ExecuteAsync(null);

        Assert.Empty(recoveryHost.FailedCleanupRecords);
        Assert.True(vm.IsAccountStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.None,
            vm.AccountDeletionRecoveryNotice);
    }

    [Fact]
    public async Task ReplacedCompletedMarker_DoesNotReuseStaleStartupFailureWitness()
    {
        var session = Session();
        var failed = Pending(session, AccountDeletionStage.LocalCleanupComplete);
        var deletion = DeletionWith(failed);
        deletion.ResumeHandler = (_, _) => Task.FromException<AccountDeletionResult>(
            new IOException("cleanup failed"));
        var recoveryHost = new AccountDeletionRecoveryHost(deletion);
        await recoveryHost.ResumeConfirmedCleanupAsync();
        var replacement = Pending(
            session with { DeviceIdentifier = Guid.NewGuid().ToString("D") },
            AccountDeletionStage.LocalCleanupComplete);
        deletion.ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
            [replacement]);
        var vm = CreateViewModel(
            deletion,
            recoveryHost: recoveryHost);

        vm.PrepareInitialLoginState(activeSession: null);

        Assert.Empty(recoveryHost.FailedCleanupRecords);
        Assert.True(vm.IsAccountStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.None,
            vm.AccountDeletionRecoveryNotice);
    }

    [Fact]
    public async Task PreparedClear_UsesExactWitnessAndRefreshesSavedAccounts()
    {
        var session = Session();
        var pending = Pending(session, AccountDeletionStage.Prepared);
        var deletion = DeletionWith(pending);
        var saved = new CountingSavedAccountService();
        var vm = CreateViewModel(deletion, saved);
        vm.PreparePersistedSession(session);

        await vm.ClearPendingAccountDeletionLocalDataAsync();

        Assert.Same(pending, Assert.Single(deletion.ClearCalls));
        Assert.True(vm.IsAccountStage);
        Assert.Equal(AccountDeletionRecoveryNoticeKind.None, vm.AccountDeletionRecoveryNotice);
        Assert.True(saved.GetAccountsCalls >= 2);
        Assert.Contains("安全隔离", vm.Status);
        Assert.DoesNotContain("重新在线登录", vm.Status);
        var marker = Assert.Single(deletion.ReadAllResult.Records);
        Assert.Equal(
            AccountDeletionStage.UncertainLocalCleanupComplete,
            marker.Stage);
        Assert.Equal(pending.EntryId, marker.EntryId);
    }

    [Fact]
    public async Task PreparedClearNotFound_ReloadsLedgerWithoutClaimingCleanup()
    {
        var session = Session();
        var pending = Pending(session, AccountDeletionStage.Prepared);
        var deletion = DeletionWith(pending);
        deletion.ClearHandler = (_, _) =>
        {
            deletion.ReadAllResult = AccountDeletionTombstoneReadAllResult.Absent;
            return Task.FromResult(new AccountDeletionLocalResolutionResult(
                AccountDeletionLocalResolutionStatus.NotFound));
        };
        var vm = CreateViewModel(deletion);
        vm.PreparePersistedSession(session);

        await vm.ClearPendingAccountDeletionLocalDataAsync();

        Assert.Same(pending, Assert.Single(deletion.ClearCalls));
        Assert.True(vm.IsAccountStage);
        Assert.Contains("记录已变化", vm.Status);
        Assert.Contains("未执行本地清理", vm.Status);
        Assert.DoesNotContain("安全隔离", vm.Status);
        Assert.DoesNotContain("本机数据已清理", vm.Status);
    }

    [Fact]
    public async Task SuccessfulCleanup_RescansAndPresentsRemainingPendingRecords()
    {
        var first = Pending(Session("account-a", "https://a.example"),
            AccountDeletionStage.Prepared);
        var second = Pending(Session("account-b", "https://b.example"),
            AccountDeletionStage.Prepared);
        var deletion = DeletionWith(first, second);
        var vm = CreateViewModel(deletion);
        vm.PrepareInitialLoginState(activeSession: null);

        await vm.ClearPendingAccountDeletionLocalDataAsync();

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Single(deletion.ClearCalls);
        Assert.Contains("继续处理", vm.Status);

        await vm.ClearPendingAccountDeletionLocalDataAsync();

        Assert.Equal(2, deletion.ClearCalls.Count);
        Assert.True(vm.IsAccountStage);
    }

    [Fact]
    public async Task LateCleanupResult_CannotReplaceNewExactRecoveryRecord()
    {
        var session = Session();
        var first = Pending(session, AccountDeletionStage.Prepared);
        var replacement = Pending(session, AccountDeletionStage.Prepared);
        var completion = new TaskCompletionSource<AccountDeletionLocalResolutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deletion = DeletionWith(first);
        deletion.ClearHandler = (_, _) => completion.Task;
        var vm = CreateViewModel(deletion);
        vm.PreparePersistedSession(session);

        var running = vm.ClearPendingAccountDeletionLocalDataAsync();
        deletion.ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
            [replacement]);
        vm.PrepareInitialLoginState(session);
        completion.SetResult(new AccountDeletionLocalResolutionResult(
            AccountDeletionLocalResolutionStatus.Completed));
        await running;

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.Prepared,
            vm.AccountDeletionRecoveryNotice);
        Assert.DoesNotContain("已清除", vm.Status);
        Assert.Same(first, Assert.Single(deletion.ClearCalls));
    }

    [Fact]
    public async Task ConfirmedCleanup_UsesExactWitnessAndCompletesRecovery()
    {
        var session = Session();
        var pending = Pending(session, AccountDeletionStage.ServerConfirmed);
        var deletion = DeletionWith(pending);
        var vm = CreateViewModel(deletion);
        vm.PreparePersistedSession(session);

        await vm.RetryConfirmedAccountDeletionCleanupCommand.ExecuteAsync(null);

        Assert.Same(pending, Assert.Single(deletion.ResumeCalls));
        Assert.True(vm.IsAccountStage);
        Assert.Equal(AccountDeletionRecoveryNoticeKind.None, vm.AccountDeletionRecoveryNotice);
        var marker = Assert.Single(deletion.ReadAllResult.Records);
        Assert.Equal(AccountDeletionStage.LocalCleanupComplete, marker.Stage);
        Assert.Equal(pending.EntryId, marker.EntryId);
        Assert.Equal(pending.Revision + 1, marker.Revision);
    }

    [Theory]
    [InlineData(AccountDeletionStage.LocalCleanupComplete)]
    [InlineData(AccountDeletionStage.UncertainLocalCleanupComplete)]
    public async Task CompletedMarkerRepurge_UsesExactWitnessAndKeepsPermanentMarker(
        AccountDeletionStage stage)
    {
        var session = Session();
        var complete = Pending(session, stage);
        var deletion = DeletionWith(complete);
        var vm = CreateViewModel(deletion);
        vm.PreparePersistedSession(session);

        await vm.RetryConfirmedAccountDeletionCleanupCommand.ExecuteAsync(null);

        Assert.Same(complete, Assert.Single(deletion.ResumeCalls));
        Assert.Same(complete, Assert.Single(deletion.ReadAllResult.Records));
        Assert.True(vm.IsAccountStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.None,
            vm.AccountDeletionRecoveryNotice);
    }

    [Fact]
    public async Task UncertainMarkerIncompleteRepurge_RetainsUncertainGuard()
    {
        var session = Session();
        var complete = Pending(
            session,
            AccountDeletionStage.UncertainLocalCleanupComplete);
        var deletion = DeletionWith(complete);
        deletion.ResumeHandler = (expected, _) =>
            Task.FromResult<AccountDeletionResult>(
                new AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete(
                    expected,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Token,
                        AccountDeletionCleanupFailureKind.Unavailable)]));
        var vm = CreateViewModel(deletion);
        vm.PreparePersistedSession(session);

        await vm.RetryConfirmedAccountDeletionCleanupCommand.ExecuteAsync(null);

        Assert.Same(complete, Assert.Single(deletion.ResumeCalls));
        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete,
            vm.AccountDeletionRecoveryNotice);
        Assert.DoesNotContain("服务器已确认", vm.AccountDeletionRecoveryMessage);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
    }

    [Fact]
    public async Task IncompleteConfirmedCleanup_RetainsGuardAndExactRecord()
    {
        var session = Session();
        var pending = Pending(session, AccountDeletionStage.ServerConfirmed);
        var deletion = DeletionWith(pending);
        deletion.ResumeHandler = (expected, _) =>
            Task.FromResult<AccountDeletionResult>(
                new AccountDeletionResult.ConfirmedLocalCleanupIncomplete(
                    expected,
                    [new AccountDeletionCleanupFailure(
                        AccountDeletionCleanupTarget.Token,
                        AccountDeletionCleanupFailureKind.Unavailable)]));
        var vm = CreateViewModel(deletion);
        vm.PreparePersistedSession(session);

        await vm.RetryConfirmedAccountDeletionCleanupCommand.ExecuteAsync(null);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.ServerConfirmed,
            vm.AccountDeletionRecoveryNotice);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
    }

    [Fact]
    public void PreparedContinueAction_SelectsAnotherAccount()
    {
        var session = Session();
        var vm = CreateViewModel(DeletionWith(Pending(
            session,
            AccountDeletionStage.Prepared)));
        vm.PreparePersistedSession(session);

        vm.ContinueFromAccountDeletionRecoveryCommand.Execute(null);

        Assert.True(vm.IsAccountStage);
        Assert.Equal(string.Empty, vm.Email);
        Assert.Contains("其他账户", vm.Status);
        Assert.False(vm.ShowUnlockPanel);
    }

    [Fact]
    public void ServerConfirmedContinueAction_SelectsAnotherAccount()
    {
        var session = Session();
        var vm = CreateViewModel(DeletionWith(Pending(
            session,
            AccountDeletionStage.ServerConfirmed)));
        vm.PreparePersistedSession(session);

        vm.ContinueFromAccountDeletionRecoveryCommand.Execute(null);

        Assert.True(vm.IsAccountStage);
        Assert.Equal(string.Empty, vm.Email);
        Assert.Equal(session.ServerUrl, vm.ServerUrl);
        Assert.Contains("其他账户", vm.Status);
    }

    [Fact]
    public void SavedAccountActivation_RechecksTombstoneBeforePreparingUnlock()
    {
        var session = Session();
        var store = new MemoryTokenStore();
        store.Save(session);
        var option = new SavedAccountOption(
            session.AccountId,
            session.Email,
            session.Endpoints!,
            IsActive: true);
        var saved = new FixedSavedAccountService(option);
        var vm = new LoginViewModel(
            new NoopAuthService(),
            demoVault: null,
            savedAccountService: saved,
            tokenStore: store,
            accountDeletion: DeletionWith(Pending(
                session,
                AccountDeletionStage.Prepared)));

        vm.SelectSavedAccountCommand.Execute(option);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.Prepared,
            vm.AccountDeletionRecoveryNotice);
    }

    [Fact]
    public async Task UnlockToctouGuard_ReturnsToRecoveryInsteadOfStayingInUnlock()
    {
        var session = Session();
        var store = new MemoryTokenStore();
        store.Save(session);
        var deletion = new ControlledAccountDeletionService();
        var auth = new NoopAuthService
        {
            UnlockResult = new AuthResult.Failure(
                "unsafe core detail",
                AuthFailureKind.AccountDeletionRecoveryRequired),
        };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            tokenStore: store,
            accountDeletion: deletion);
        vm.PrepareInitialLoginState(session);
        Assert.True(vm.IsUnlockStage);

        deletion.ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(
            [Pending(session, AccountDeletionStage.Prepared)]);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsAccountDeletionRecoveryStage);
        Assert.Equal(
            AccountDeletionRecoveryNoticeKind.Prepared,
            vm.AccountDeletionRecoveryNotice);
        Assert.DoesNotContain("unsafe core detail", vm.Status);
        Assert.Contains("旧令牌", vm.Status);
    }

    [Fact]
    public void LoginPage_UsesGuardAndRequiresPreparedCleanupConfirmation()
    {
        var source = Read("src", "App", "Views", "LoginPage.xaml.cs");
        var xaml = XDocument.Parse(Read(
            "src",
            "App",
            "Views",
            "LoginPage.xaml"));

        Assert.Contains("ViewModel.PrepareInitialLoginState(session)", source);
        Assert.Contains("PrepareInitialLoginState(activeSession: null)", source);
        Assert.DoesNotContain("ViewModel.PrepareUnlock(session", source);
        Assert.Contains("仅清除此设备上的数据？", source);
        Assert.Contains("永久安全隔离记录", source);
        Assert.Contains("永久禁用此账户标识与当前服务器端点组合", source);
        Assert.Contains("Windows 应用重置", source);
        Assert.Contains("全部本地数据", source);
        Assert.Contains("仍可能稍后在服务器完成", source);
        Assert.Contains("不会再次向服务器发送删除请求", source);
        Assert.Contains("ClearPendingAccountDeletionLocalDataAsync", source);

        var stage = Named(xaml, "AccountDeletionRecoveryStage");
        Assert.Contains("IsAccountDeletionRecoveryStage", stage.Attribute("Visibility")?.Value);
        Assert.Equal(
            "OnClearPendingAccountDeletionClick",
            Named(xaml, "ClearPendingAccountDeletionLocalDataButton")
                .Attribute("Click")?.Value);
        Assert.Contains(
            "RetryConfirmedAccountDeletionCleanupCommand",
            Named(xaml, "RetryConfirmedAccountDeletionCleanupButton")
                .Attribute("Command")?.Value);
    }

    [Fact]
    public void FullLocalReset_RequiresTypedConfirmationAndProductionDependencies()
    {
        var source = Read("src", "App", "Views", "LoginPage.xaml.cs");
        var viewModelSource = Read(
            "src",
            "App",
            "ViewModels",
            "LoginViewModel.cs");
        var xaml = XDocument.Parse(Read(
            "src",
            "App",
            "Views",
            "LoginPage.xaml"));
        var services = Read(
            "src",
            "App",
            "Services",
            "ServiceConfiguration.cs");

        var button = Named(xaml, "RequestFullLocalResetButton");
        Assert.Equal("清除全部本机数据并退出", button.Attribute("Content")?.Value);
        Assert.Equal("OnRequestFullLocalResetClick", button.Attribute("Click")?.Value);
        Assert.Contains("CanRequestFullLocalReset", button.Attribute("IsEnabled")?.Value);
        Assert.Contains("ShowFullLocalResetAction", button.Attribute("Visibility")?.Value);
        Assert.Contains("SystemFillColorCriticalBrush", button.Attribute("Foreground")?.Value);

        Assert.Contains("所有保存账户", source);
        Assert.Contains("离线密码库", source);
        Assert.Contains("设备密钥", source);
        Assert.Contains("应用偏好", source);
        Assert.Contains("永久删除隔离记录", source);
        Assert.Contains("现在会记录重置请求并退出应用", source);
        Assert.Contains("下次启动、登录界面出现前完成删除", source);
        Assert.Contains("服务器上的账户和密码库数据不会受到影响", source);
        Assert.Contains("浏览器集成可能需要重新配置", source);
        Assert.Contains("输入 RESET 以确认", source);
        Assert.Contains("MaxLength = 5", source);
        Assert.Contains("StringComparison.Ordinal", source);
        Assert.Contains("IsPrimaryButtonEnabled = false", source);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", source);
        Assert.Contains("DialogHelper.ShowTrackedAsync", source);
        Assert.Contains("_fullLocalResetConfirmationCancellation?.Cancel()", source);
        Assert.Contains("RequestFullLocalResetCommand.Cancel()", viewModelSource);

        Assert.Contains(
            "AddSingleton<ILocalDataResetService, LocalDataResetService>()",
            services);
        Assert.Contains("GetRequiredService<ILocalDataResetService>()", services);
        Assert.Contains("GetRequiredService<IAppLifecycleCoordinator>()", services);
    }

    private static LoginViewModel CreateViewModel(
        ControlledAccountDeletionService deletion,
        ISavedAccountService? saved = null,
        AccountDeletionRecoveryHost? recoveryHost = null,
        ILocalDataResetService? localDataReset = null,
        IAppLifecycleCoordinator? lifecycle = null) => new(
        new NoopAuthService(),
        demoVault: null,
        savedAccountService: saved,
        accountDeletion: deletion,
        accountDeletionRecoveryHost: recoveryHost,
        localDataReset: localDataReset,
        lifecycle: lifecycle);

    private static ControlledAccountDeletionService DeletionWith(
        params AccountDeletionTombstoneRecord[] pending) => new()
        {
            ReadAllResult = AccountDeletionTombstoneReadAllResult.Found(pending),
        };

    private static PersistedSession Session(
        string accountId = "account-a",
        string serverUrl = "https://vault.example") => new(
        PersistedSession.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        Guid.NewGuid().ToString("D"),
        "refresh-token",
        "protected-user-key",
        KdfType.Pbkdf2,
        600_000,
        null,
        null);

    private static AccountDeletionTombstoneRecord Pending(
        PersistedSession session,
        AccountDeletionStage stage)
    {
        var now = DateTimeOffset.UtcNow;
        return new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            stage switch
            {
                AccountDeletionStage.Prepared => 1,
                AccountDeletionStage.ServerConfirmed => 2,
                AccountDeletionStage.LocalCleanupComplete => 3,
                AccountDeletionStage.UncertainLocalCleanupComplete => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(stage)),
            },
            now,
            stage == AccountDeletionStage.Prepared ? now : now.AddSeconds(1),
            new AccountDeletionBinding(
                session.AccountId,
                session.Email,
                session.Endpoints!,
                session.DeviceIdentifier),
            stage);
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && attribute.Value == name));

    private static string Read(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException(parts[^1]);
    }

    private sealed class CountingSavedAccountService : ISavedAccountService
    {
        public int GetAccountsCalls { get; private set; }

        public IReadOnlyList<SavedAccountOption> GetAccounts()
        {
            GetAccountsCalls++;
            return [];
        }

        public bool TryActivate(SavedAccountOption account) => false;
    }

    private sealed class FixedSavedAccountService(SavedAccountOption account)
        : ISavedAccountService
    {
        public IReadOnlyList<SavedAccountOption> GetAccounts() => [account];

        public bool TryActivate(SavedAccountOption candidate) =>
            candidate == account;
    }

    private sealed class ControlledLocalDataResetService : ILocalDataResetService
    {
        public LocalDataResetRequestResult Result { get; set; } = new(
            LocalDataResetRequestStatus.Unavailable);

        public Action? OnRequest { get; set; }
        public int Requests { get; private set; }
        public Exception? RequestFailure { get; set; }

        public LocalDataResetRequestResult RequestReset()
        {
            Requests++;
            OnRequest?.Invoke();
            if (RequestFailure is not null)
                throw RequestFailure;
            return Result;
        }
    }

    private sealed class RecordingLifecycle : IAppLifecycleCoordinator
    {
        public bool IsTransitioning => false;
        public List<AppLifecycleIntent> Requests { get; } = [];
        public int FailClosedCalls { get; private set; }
        public Exception? RequestFailure { get; set; }

        public Task RequestAsync(
            AppLifecycleIntent intent,
            VaultSessionLease? expectedSession = null)
        {
            Requests.Add(intent);
            return RequestFailure is null
                ? Task.CompletedTask
                : Task.FromException(RequestFailure);
        }

        public Task<bool> RequestIfAsync(
            AppLifecycleIntent intent,
            Func<bool> predicate)
        {
            if (!predicate())
                return Task.FromResult(false);
            Requests.Add(intent);
            return Task.FromResult(true);
        }

        public bool MarkSessionReady() => true;

        public bool TryRunSensitive(Func<bool> action) => action();

        public void FailClosedSynchronously() => FailClosedCalls++;
    }

    private sealed class NoopAuthService : IAuthService
    {
        private readonly VaultSession _session = new();

        public AuthResult LoginResult { get; set; } =
            new AuthResult.Failure("not configured");
        public AuthResult UnlockResult { get; set; } =
            new AuthResult.Failure("not configured");
        public int LockCalls { get; private set; }

        public AuthenticationAttemptLease BeginAuthenticationAttempt() =>
            _session.BeginAuthenticationAttempt();

        public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt) =>
            _session.AbandonAuthenticationAttempt(attempt);

        public Task<AuthResult> LoginAsync(
            AuthenticationAttemptLease attempt,
            string serverUrl,
            string email,
            string masterPassword,
            CancellationToken ct = default) => Task.FromResult(LoginResult);

        public Task<AuthResult> SubmitTwoFactorAsync(
            AuthenticationAttemptLease attempt,
            string code,
            int provider,
            CancellationToken ct = default,
            bool rememberDevice = true) => Task.FromResult<AuthResult>(
                new AuthResult.Failure("not configured"));

        public Task<bool> ResendTwoFactorEmailAsync(
            AuthenticationAttemptLease attempt,
            CancellationToken ct = default) => Task.FromResult(false);

        public Task<AuthResult> UnlockAsync(
            AuthenticationAttemptLease attempt,
            string masterPassword,
            CancellationToken ct = default) => Task.FromResult(UnlockResult);

        public Task<AuthResult> UnlockWithPinAsync(
            AuthenticationAttemptLease attempt,
            string pin,
            CancellationToken ct = default) => Task.FromResult<AuthResult>(
                new AuthResult.Failure("not configured"));

        public Task<AuthResult> UnlockWithHelloAsync(
            AuthenticationAttemptLease attempt,
            byte[] signature,
            CancellationToken ct = default) => Task.FromResult<AuthResult>(
                new AuthResult.Failure("not configured"));

        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task LockAsync(
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        {
            LockCalls++;
            return Task.CompletedTask;
        }

        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> LogoutAsync(
            VaultSessionLease expectedSession,
            CancellationToken ct = default) => Task.FromResult(true);
    }
}
