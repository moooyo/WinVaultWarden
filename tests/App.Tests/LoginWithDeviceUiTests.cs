using System.Xml.Linq;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Core.Session;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class LoginWithDeviceUiTests
{
    private const string ServerUrl = "https://vault.example";
    private const string Email = "me@example.com";
    private const string RequestId = "request-id-that-must-stay-private";
    private const string QuarantineRequestId = "7a8c3e5d-0f73-4bd4-8e4c-3dc69be5379f";
    private const string Fingerprint = "alpha-bravo-charlie-delta-echo";

    [Fact]
    public async Task PasswordStage_ShowsDeviceLoginOnlyAfterKnownDeviceCheckSucceeds()
    {
        var auth = new FakeAuthService { IsAvailable = true };
        var vm = await CreatePasswordStageAsync(auth);

        Assert.True(vm.IsLoginWithDeviceAvailable);
        Assert.True(vm.ShowLoginWithDeviceOption);
        Assert.True(vm.CanStartLoginWithDevice);
        Assert.Equal(ServerUrl, auth.AvailabilityCall?.ServerUrl);
        Assert.Equal(Email, auth.AvailabilityCall?.Email);
    }

    [Fact]
    public async Task QuarantineLookupInProgress_KeepsEntryClosedWithoutFlashingWarning()
    {
        var lookup = NewSource<LoginWithDeviceQuarantineInfo>();
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            QuarantineTask = lookup.Task,
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = ServerUrl,
            Email = Email,
        };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.True(vm.IsLoginWithDeviceQuarantineBlocked);
        Assert.False(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.False(vm.ShowLoginWithDeviceOption);
        lookup.SetResult(new LoginWithDeviceQuarantineInfo.Absent());
        await WaitUntilAsync(
            () => vm.ShowLoginWithDeviceOption,
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DurableQuarantineBeforeEligibility_HidesEntryWithoutLeakingRequestId()
    {
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            QuarantineInfo = QuarantineFound(isClearEligible: false),
        };

        var vm = await CreatePasswordStageAsync(auth);

        Assert.True(vm.IsLoginWithDeviceQuarantineBlocked);
        Assert.True(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.False(vm.ShowLoginWithDeviceQuarantineClearAction);
        Assert.False(vm.CanClearLoginWithDeviceQuarantine);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.False(vm.ShowLoginWithDeviceOption);
        Assert.Equal(0, auth.AvailabilityCalls);
        Assert.Equal(1, auth.QuarantineReadCalls);
        Assert.Contains("主密码", vm.LoginWithDeviceQuarantineMessage, StringComparison.Ordinal);
        Assert.Contains("企业 SSO", vm.LoginWithDeviceQuarantineMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(
            QuarantineRequestId,
            vm.LoginWithDeviceQuarantineMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableQuarantineAfterEligibility_OffersExactClearAction()
    {
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            QuarantineInfo = QuarantineFound(isClearEligible: true),
        };

        var vm = await CreatePasswordStageAsync(auth);

        Assert.True(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.True(vm.ShowLoginWithDeviceQuarantineClearAction);
        Assert.True(vm.CanClearLoginWithDeviceQuarantine);
        Assert.False(vm.ShowLoginWithDeviceOption);
        Assert.Contains("撤销其他设备", vm.LoginWithDeviceQuarantineMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(
            QuarantineRequestId,
            vm.LoginWithDeviceQuarantineMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactClearSuccess_RechecksKnownDeviceBeforeReopeningEntry()
    {
        var expected = QuarantineFound(isClearEligible: true);
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            QuarantineInfo = expected,
            ClearResult = new LoginWithDeviceQuarantineClearResult(
                LoginWithDeviceQuarantineClearStatus.Cleared),
        };
        var vm = await CreatePasswordStageAsync(auth);

        await vm.ClearLoginWithDeviceQuarantineCommand.ExecuteAsync(null);

        Assert.Same(expected, auth.ClearCall?.Expected);
        Assert.Equal(ServerUrl, auth.ClearCall?.ServerUrl);
        Assert.Equal(Email, auth.ClearCall?.Email);
        Assert.False(vm.IsLoginWithDeviceQuarantineBlocked);
        Assert.False(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.True(vm.IsLoginWithDeviceAvailable);
        Assert.True(vm.ShowLoginWithDeviceOption);
        Assert.Equal(1, auth.AvailabilityCalls);
        Assert.Equal(LoginStatusKind.Success, vm.StatusKind);
    }

    [Fact]
    public async Task ExactClearChanged_RefreshesAndKeepsDeviceLoginBlocked()
    {
        var expected = QuarantineFound(isClearEligible: true);
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            QuarantineInfo = expected,
            ClearResult = new LoginWithDeviceQuarantineClearResult(
                LoginWithDeviceQuarantineClearStatus.Changed),
        };
        var vm = await CreatePasswordStageAsync(auth);

        await vm.ClearLoginWithDeviceQuarantineCommand.ExecuteAsync(null);

        Assert.Same(expected, auth.ClearCall?.Expected);
        Assert.True(vm.IsLoginWithDeviceQuarantineBlocked);
        Assert.True(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.False(vm.ShowLoginWithDeviceOption);
        Assert.Equal(0, auth.AvailabilityCalls);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
    }

    [Fact]
    public async Task CorruptAndUnavailableQuarantineStates_FailClosedWithoutClearAction()
    {
        LoginWithDeviceQuarantineInfo[] states =
        [
            new LoginWithDeviceQuarantineInfo.Corrupt(),
            new LoginWithDeviceQuarantineInfo.Unavailable(),
        ];

        foreach (var state in states)
        {
            var auth = new FakeAuthService
            {
                IsAvailable = true,
                QuarantineInfo = state,
            };
            var vm = await CreatePasswordStageAsync(auth);

            Assert.True(vm.IsLoginWithDeviceQuarantineBlocked);
            Assert.True(vm.ShowLoginWithDeviceQuarantineNotice);
            Assert.False(vm.ShowLoginWithDeviceQuarantineClearAction);
            Assert.False(vm.CanClearLoginWithDeviceQuarantine);
            Assert.False(vm.ShowLoginWithDeviceOption);
            Assert.Equal(0, auth.AvailabilityCalls);
        }
    }

    [Fact]
    public async Task PrepareAccountAndPageRebuild_CannotClearDurableQuarantineLocally()
    {
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            QuarantineInfo = QuarantineFound(isClearEligible: false),
        };
        var first = await CreatePasswordStageAsync(auth);

        first.PrepareAccount(ServerUrl, Email);
        await first.LoginCommand.ExecuteAsync(null);
        var rebuilt = await CreatePasswordStageAsync(auth);

        Assert.True(first.IsLoginWithDeviceQuarantineBlocked);
        Assert.True(rebuilt.IsLoginWithDeviceQuarantineBlocked);
        Assert.False(first.ShowLoginWithDeviceOption);
        Assert.False(rebuilt.ShowLoginWithDeviceOption);
        Assert.Equal(0, auth.ClearCalls);
        Assert.Equal(0, auth.AvailabilityCalls);
        Assert.Equal(3, auth.QuarantineReadCalls);
    }

    [Fact]
    public async Task CreateResponseLost_RemainsRebuildableAndDoesNotEnterQuarantineUi()
    {
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            BeginResult = new AuthResult.Failure(
                "create response lost",
                AuthFailureKind.Network),
        };
        var vm = await CreatePasswordStageAsync(auth);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.Equal(LoginWithDeviceTerminalKind.Failed, vm.LoginWithDeviceTerminal);
        Assert.False(vm.IsLoginWithDeviceQuarantineBlocked);
        Assert.False(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.True(vm.ShowLoginWithDeviceOption);
        Assert.Equal(0, auth.ClearCalls);
        Assert.DoesNotContain("隔离", vm.Status, StringComparison.Ordinal);
        Assert.Contains("可能已经发送", vm.Status, StringComparison.Ordinal);
        Assert.Contains("新页面显示的指纹短语", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownDeviceCheck_FailsClosedWhenLookupThrows()
    {
        var auth = new FakeAuthService
        {
            AvailabilityException = new HttpRequestException("offline"),
        };

        var vm = await CreatePasswordStageAsync(auth);

        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.False(vm.ShowLoginWithDeviceOption);
        Assert.False(vm.CanStartLoginWithDevice);
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task StaleKnownDeviceResult_CannotReopenEntryAfterReturningToAccount()
    {
        var lookup = NewSource<bool>();
        var auth = new FakeAuthService { AvailabilityTask = lookup.Task };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = ServerUrl,
            Email = Email,
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.BackCommand.Execute(null);
        lookup.SetResult(true);
        await Task.Yield();

        Assert.True(vm.IsAccountStage);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.False(vm.ShowLoginWithDeviceOption);
    }

    [Fact]
    public async Task WaitingStage_ShowsFingerprintExpiryBusyStateAndCancelFocus()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var poll = NewSource<AuthResult>();
        var auth = DeviceReadyAuth(expiresAt, (_, _) => poll.Task);
        var vm = await CreatePasswordStageAsync(auth);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsLoginWithDeviceStage);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ShowPrimaryAction);
        Assert.True(vm.ShowCancelLoginWithDevice);
        Assert.Equal(Fingerprint, vm.LoginWithDeviceFingerprint);
        Assert.Equal(expiresAt, vm.LoginWithDeviceExpiresAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(vm.LoginWithDeviceExpiryText));
        Assert.Equal(LoginFocusTarget.LoginWithDeviceCancel, vm.FocusTarget);

        vm.CancelLoginWithDeviceCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApprovalSuccess_UsesSharedSuccessTransition()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var expectedLease = TestSessionLeases.CreateSessionLease();
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.Success(expectedLease)));
        var vm = await CreatePasswordStageAsync(auth);
        VaultSessionLease? completed = null;
        vm.SetSuccessCallback(lease => completed = lease);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.Same(expectedLease, completed);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.LoginWithDeviceFingerprint);
        Assert.Null(vm.LoginWithDeviceExpiresAtUtc);
        Assert.Equal(LoginWithDeviceTerminalKind.None, vm.LoginWithDeviceTerminal);
    }

    [Fact]
    public async Task ApprovalRequiringTwoFactor_UsesSharedTwoFactorAndSuccessTransitions()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var expectedLease = TestSessionLeases.CreateSessionLease();
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.TwoFactorRequired([0])));
        auth.SubmitResult = new AuthResult.Success(expectedLease);
        var vm = await CreatePasswordStageAsync(auth);
        VaultSessionLease? completed = null;
        vm.SetSuccessCallback(lease => completed = lease);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.True(vm.IsLoginWithDeviceQuarantineBlocked);
        Assert.False(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.Equal(string.Empty, vm.LoginWithDeviceFingerprint);
        Assert.Equal(expiresAt, vm.LoginWithDeviceExpiresAtUtc);
        Assert.True(vm.ShowLoginWithDeviceTwoFactorExpiry);
        vm.TwoFactorCode = "123456";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Same(expectedLease, completed);
        Assert.Equal("123456", auth.SubmittedTwoFactorCode);
        Assert.Null(vm.LoginWithDeviceExpiresAtUtc);
    }

    [Fact]
    public async Task ReturningFromDeviceTwoFactor_RechecksDurableStateBeforeReopeningEntry()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.TwoFactorRequired([0])));
        var vm = await CreatePasswordStageAsync(auth);
        await vm.LoginWithDeviceCommand.ExecuteAsync(null);
        auth.QuarantineInfo = new LoginWithDeviceQuarantineInfo.Unavailable();

        vm.BackCommand.Execute(null);

        Assert.True(vm.IsPasswordStage);
        Assert.True(vm.IsLoginWithDeviceQuarantineBlocked);
        Assert.True(vm.ShowLoginWithDeviceQuarantineNotice);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.False(vm.ShowLoginWithDeviceOption);
        Assert.Equal(1, auth.AvailabilityCalls);
        Assert.Equal(2, auth.QuarantineReadCalls);
    }

    [Fact]
    public async Task DeviceTwoFactor_ExpiresProactivelyAndClearsChallenge()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(1);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.TwoFactorRequired([0])));
        var vm = await CreatePasswordStageAsync(auth);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.NotEmpty(vm.TwoFactorProviderOptions);
        vm.TwoFactorCode = "123456";
        await WaitUntilAsync(() => vm.IsPasswordStage, TimeSpan.FromSeconds(5));

        Assert.False(vm.IsTwoFactorStage);
        Assert.Empty(vm.TwoFactorProviderOptions);
        Assert.Equal(string.Empty, vm.TwoFactorCode);
        Assert.Null(vm.LoginWithDeviceExpiresAtUtc);
        Assert.Equal(LoginWithDeviceTerminalKind.Expired, vm.LoginWithDeviceTerminal);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
        Assert.Contains("已过期", vm.Status, StringComparison.Ordinal);
        Assert.True(auth.AbandonCalls > 0);
    }

    [Fact]
    public async Task DeactivateDuringDeviceTwoFactor_LocksLateCommittedSuccess()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var submit = NewSource<AuthResult>();
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.TwoFactorRequired([0])));
        auth.SubmitTask = submit.Task;
        var vm = await CreatePasswordStageAsync(auth);
        vm.SetSuccessHandler(_ => true);
        await vm.LoginWithDeviceCommand.ExecuteAsync(null);
        vm.TwoFactorCode = "123456";

        var running = vm.LoginCommand.ExecuteAsync(null);
        await auth.SubmitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Deactivate();

        Assert.True(auth.LastSubmitCancellationToken.IsCancellationRequested);
        var lateLease = TestSessionLeases.CreateSessionLease();
        submit.SetResult(new AuthResult.Success(lateLease));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await auth.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, auth.LockCalls);
        Assert.Same(lateLease, auth.LastLockedLease);
        Assert.True(vm.IsPasswordStage);
        Assert.Empty(vm.TwoFactorProviderOptions);
    }

    [Fact]
    public async Task Cancel_ReturnsImmediatelyAndLateApprovalCannotNavigate()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var poll = NewSource<AuthResult>();
        var auth = DeviceReadyAuth(expiresAt, (_, _) => poll.Task);
        var vm = await CreatePasswordStageAsync(auth);
        var successCount = 0;
        vm.SetSuccessCallback(_ => successCount++);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelLoginWithDeviceCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsPasswordStage);
        Assert.False(vm.IsBusy);
        Assert.Equal(LoginWithDeviceTerminalKind.Canceled, vm.LoginWithDeviceTerminal);
        Assert.Equal(LoginStatusKind.Info, vm.StatusKind);
        Assert.Equal(1, auth.CancelCalls);
        Assert.Equal(0, successCount);

        var lateLease = TestSessionLeases.CreateSessionLease();
        poll.SetResult(new AuthResult.Success(lateLease));
        await auth.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsPasswordStage);
        Assert.Equal(LoginWithDeviceTerminalKind.Canceled, vm.LoginWithDeviceTerminal);
        Assert.Equal(0, successCount);
        Assert.Equal(1, auth.LockCalls);
        Assert.Same(lateLease, auth.LastLockedLease);
    }

    [Fact]
    public async Task Deactivate_CancelsPendingPollAndLocksLateApproval()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var poll = NewSource<AuthResult>();
        var auth = DeviceReadyAuth(expiresAt, (_, _) => poll.Task);
        var vm = await CreatePasswordStageAsync(auth);
        var successCount = 0;
        vm.SetSuccessHandler(_ =>
        {
            successCount++;
            return true;
        });

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Deactivate();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        var lateLease = TestSessionLeases.CreateSessionLease();
        poll.SetResult(new AuthResult.Success(lateLease));
        await auth.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, successCount);
        Assert.Equal(1, auth.LockCalls);
        Assert.Same(lateLease, auth.LastLockedLease);
        Assert.True(vm.IsPasswordStage);
    }

    [Fact]
    public async Task Deactivate_LocksEveryLateLeaseBearingAuthResult()
    {
        Func<VaultSessionLease, AuthResult>[] factories =
        [
            lease => new AuthResult.SsoAuthenticated(lease),
            lease => new AuthResult.TrustedDeviceApprovalRequired(
                lease,
                HasAdminApproval: false),
            lease => new AuthResult.TdeV2JitProvisioningRequired(
                lease,
                "organization-id"),
            lease => new AuthResult.PasswordChangeRequired(
                lease,
                RequiredPasswordChangeReason.AdministratorTemporaryPassword,
                Policy: null),
        ];

        foreach (var factory in factories)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            var poll = NewSource<AuthResult>();
            var auth = DeviceReadyAuth(expiresAt, (_, _) => poll.Task);
            var vm = await CreatePasswordStageAsync(auth);
            vm.SetSuccessHandler(_ => true);

            var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
            await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.Deactivate();
            await running.WaitAsync(TimeSpan.FromSeconds(5));

            var lateLease = TestSessionLeases.CreateSessionLease();
            poll.SetResult(factory(lateLease));
            await auth.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, auth.LockCalls);
            Assert.Same(lateLease, auth.LastLockedLease);
        }
    }

    [Fact]
    public async Task StalePollResult_IsLockedBeforeItIsDiscarded()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var poll = NewSource<AuthResult>();
        var auth = DeviceReadyAuth(expiresAt, (_, _) => poll.Task);
        var vm = await CreatePasswordStageAsync(auth);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.PrepareAccount(ServerUrl, Email);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        var staleLease = TestSessionLeases.CreateSessionLease();
        poll.SetResult(new AuthResult.Success(staleLease));
        await auth.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, auth.LockCalls);
        Assert.Same(staleLease, auth.LastLockedLease);
        Assert.True(vm.IsAccountStage);
    }

    [Fact]
    public async Task AcceptedSuccess_CanDeactivateDuringNavigationWithoutRelockingSession()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var acceptedLease = TestSessionLeases.CreateSessionLease();
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.Success(acceptedLease)));
        var vm = await CreatePasswordStageAsync(auth);
        var successCount = 0;
        vm.SetSuccessHandler(lease =>
        {
            Assert.Same(acceptedLease, lease);
            successCount++;
            vm.Deactivate();
            return true;
        });

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, successCount);
        Assert.Equal(0, auth.LockCalls);
        Assert.Equal(0, auth.ConditionalLogoutCalls);
    }

    [Fact]
    public async Task RejectedSuccessHandler_LocksSessionAndReturnsToUnlock()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var rejectedLease = TestSessionLeases.CreateSessionLease();
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.Success(rejectedLease)));
        var vm = await CreatePasswordStageAsync(auth);
        vm.SetSuccessHandler(_ => false);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.LockCalls);
        Assert.Same(rejectedLease, auth.LastLockedLease);
        Assert.True(vm.IsUnlockStage);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
        Assert.Contains("重新锁定", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedSuccessHandler_FallsBackToConditionalLogoutWhenLockFails()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var rejectedLease = TestSessionLeases.CreateSessionLease();
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.Success(rejectedLease)));
        auth.LockException = new InvalidOperationException("lock failed");
        var vm = await CreatePasswordStageAsync(auth);
        vm.SetSuccessHandler(_ => false);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.LockCalls);
        Assert.Equal(1, auth.ConditionalLogoutCalls);
        Assert.Same(rejectedLease, auth.LastLoggedOutLease);
        Assert.True(vm.IsUnlockStage);
    }

    [Fact]
    public async Task Cancel_DropsLateBeginResultBeforePollingStarts()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var begin = NewSource<AuthResult>();
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            BeginTask = begin.Task,
        };
        var vm = await CreatePasswordStageAsync(auth);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.BeginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelLoginWithDeviceCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        begin.SetResult(new AuthResult.LoginWithDeviceWaiting(
            RequestId,
            Fingerprint,
            expiresAt));
        await Task.Yield();

        Assert.True(vm.IsPasswordStage);
        Assert.Equal(LoginWithDeviceTerminalKind.Canceled, vm.LoginWithDeviceTerminal);
        Assert.Equal(0, auth.PollCalls);
        Assert.Equal(1, auth.CancelCalls);
    }

    [Fact]
    public async Task CancelDuringBegin_LocksLateCommittedSuccess()
    {
        var begin = NewSource<AuthResult>();
        var auth = new FakeAuthService
        {
            IsAvailable = true,
            BeginTask = begin.Task,
        };
        var vm = await CreatePasswordStageAsync(auth);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.BeginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelLoginWithDeviceCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        var lateLease = TestSessionLeases.CreateSessionLease();
        begin.SetResult(new AuthResult.Success(lateLease));
        await auth.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, auth.LockCalls);
        Assert.Same(lateLease, auth.LastLockedLease);
        Assert.Equal(0, auth.PollCalls);
        Assert.True(vm.IsPasswordStage);
    }

    [Theory]
    [InlineData(LoginWithDeviceUiStatus.Denied, LoginWithDeviceTerminalKind.Denied)]
    [InlineData(LoginWithDeviceUiStatus.Expired, LoginWithDeviceTerminalKind.Expired)]
    public async Task ServerTerminalStatus_IsClassifiedAndReturnsToPassword(
        LoginWithDeviceUiStatus status,
        LoginWithDeviceTerminalKind expected)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(Pending(expiresAt, status)));
        var vm = await CreatePasswordStageAsync(auth);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.Equal(expected, vm.LoginWithDeviceTerminal);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
        Assert.False(string.IsNullOrWhiteSpace(vm.Status));
        Assert.Equal(0, auth.CancelCalls);
    }

    [Fact]
    public async Task MismatchedPollBinding_FailsClosedWithoutDisplayingRequestId()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.LoginWithDevicePending(
                "different-request",
                Fingerprint,
                expiresAt,
                LoginWithDeviceUiStatus.Pending)));
        var vm = await CreatePasswordStageAsync(auth);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.Equal(LoginWithDeviceTerminalKind.Failed, vm.LoginWithDeviceTerminal);
        Assert.Equal("服务器响应无效，无法完成认证。", vm.Status);
        Assert.DoesNotContain(RequestId, vm.Status, StringComparison.Ordinal);
        Assert.Equal(1, auth.CancelCalls);
    }

    [Fact]
    public async Task PendingPoll_IsCanceledDuringBoundedDelay()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(Pending(
                expiresAt,
                LoginWithDeviceUiStatus.Pending)));
        var vm = await CreatePasswordStageAsync(auth);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelLoginWithDeviceCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, auth.PollCalls);
        Assert.Equal(1, auth.CancelCalls);
        Assert.True(vm.IsPasswordStage);
    }

    [Theory]
    [InlineData(AuthFailureKind.Network)]
    [InlineData(AuthFailureKind.Timeout)]
    public async Task ReadOnlyPollTransientFailure_KeepsRequestPending(
        AuthFailureKind failureKind)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(new AuthResult.Failure(
                "transient detail that must not be shown",
                failureKind)));
        var vm = await CreatePasswordStageAsync(auth);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsLoginWithDeviceStage);
        Assert.True(vm.IsBusy);
        Assert.Equal(LoginWithDeviceTerminalKind.None, vm.LoginWithDeviceTerminal);
        Assert.Equal(0, auth.CancelCalls);
        Assert.Contains("正在重试", vm.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("transient detail", vm.Status, StringComparison.Ordinal);

        vm.CancelLoginWithDeviceCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TokenExchangeOutcomeUncertain_TerminatesWithoutAutomaticRetry()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.LoginWithDeviceOutcomeUncertain()));
        var vm = await CreatePasswordStageAsync(auth);

        await vm.LoginWithDeviceCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.False(vm.IsBusy);
        Assert.Equal(
            LoginWithDeviceTerminalKind.OutcomeUncertain,
            vm.LoginWithDeviceTerminal);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
        Assert.Contains("结果无法确认", vm.Status, StringComparison.Ordinal);
        Assert.Contains("不要重试", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.False(vm.ShowLoginWithDeviceOption);
        Assert.Equal(0, auth.CancelCalls);
    }

    [Fact]
    public async Task DeviceTwoFactorOutcomeUncertain_UsesSameTerminalTransition()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.TwoFactorRequired([0])));
        auth.SubmitResult = new AuthResult.LoginWithDeviceOutcomeUncertain();
        var vm = await CreatePasswordStageAsync(auth);
        await vm.LoginWithDeviceCommand.ExecuteAsync(null);
        vm.TwoFactorCode = "123456";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.False(vm.IsBusy);
        Assert.Equal(
            LoginWithDeviceTerminalKind.OutcomeUncertain,
            vm.LoginWithDeviceTerminal);
        Assert.Contains("结果无法确认", vm.Status, StringComparison.Ordinal);
        Assert.Contains("不要重试", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.Empty(vm.TwoFactorProviderOptions);
        Assert.Equal(string.Empty, vm.TwoFactorCode);
        Assert.Null(vm.LoginWithDeviceExpiresAtUtc);
    }

    [Fact]
    public async Task OutcomeUncertain_QuarantinesSameBindingAfterBackAndContinue()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var auth = DeviceReadyAuth(
            expiresAt,
            (_, _) => Task.FromResult<AuthResult>(
                new AuthResult.LoginWithDeviceOutcomeUncertain()));
        var vm = await CreatePasswordStageAsync(auth);
        await vm.LoginWithDeviceCommand.ExecuteAsync(null);
        var availabilityCalls = auth.AvailabilityCalls;

        vm.BackCommand.Execute(null);
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.False(vm.ShowLoginWithDeviceOption);
        Assert.Equal(availabilityCalls, auth.AvailabilityCalls);
    }

    [Fact]
    public async Task CancelDuringTokenExchange_UsesConservativeOutcomeFromAuthService()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var poll = NewSource<AuthResult>();
        var auth = DeviceReadyAuth(expiresAt, (_, _) => poll.Task);
        auth.CancellationStatus =
            LoginWithDeviceCancellationStatus.OutcomeUncertain;
        var vm = await CreatePasswordStageAsync(auth);

        var running = vm.LoginWithDeviceCommand.ExecuteAsync(null);
        await auth.PollStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.CancelLoginWithDeviceCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsPasswordStage);
        Assert.Equal(
            LoginWithDeviceTerminalKind.OutcomeUncertain,
            vm.LoginWithDeviceTerminal);
        Assert.Contains("结果无法确认", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsLoginWithDeviceAvailable);
        Assert.Equal(1, auth.CancelCalls);
    }

    [Fact]
    public void Xaml_HasDedicatedSafeDeviceLoginSurface()
    {
        var document = LoadXaml("LoginPage.xaml");
        var stage = Named(document, "LoginWithDeviceStage");
        var entry = Named(document, "LoginWithDeviceButton");
        var quarantinePanel = Named(document, "LoginWithDeviceQuarantinePanel");
        var quarantineInfo = Named(document, "LoginWithDeviceQuarantineInfoBar");
        var quarantineClear = Named(document, "ClearLoginWithDeviceQuarantineButton");
        var fingerprint = Named(document, "LoginWithDeviceFingerprintBox");
        var expiry = Named(document, "LoginWithDeviceExpiryText");
        var cancel = Named(document, "CancelLoginWithDeviceButton");
        var twoFactorExpiry = Named(
            document,
            "LoginWithDeviceTwoFactorExpiryInfoBar");
        var primary = Named(document, "PrimaryAuthButton");

        Assert.Contains("IsLoginWithDeviceStage", stage.Attribute("Visibility")?.Value);
        Assert.Contains("ShowLoginWithDeviceOption", entry.Attribute("Visibility")?.Value);
        Assert.Contains(
            "ShowLoginWithDeviceQuarantineNotice",
            quarantinePanel.Attribute("Visibility")?.Value);
        Assert.Contains(
            "LoginWithDeviceQuarantineTitle",
            quarantineInfo.Attribute("Title")?.Value);
        Assert.Contains(
            "LoginWithDeviceQuarantineMessage",
            quarantineInfo.Attribute("Message")?.Value);
        Assert.Equal("False", quarantineInfo.Attribute("IsClosable")?.Value);
        Assert.Equal(
            "检查/撤销其他设备会话后清除此隔离",
            quarantineClear.Attribute("Content")?.Value);
        Assert.Contains(
            "ShowLoginWithDeviceQuarantineClearAction",
            quarantineClear.Attribute("Visibility")?.Value);
        Assert.Contains(
            "CanClearLoginWithDeviceQuarantine",
            quarantineClear.Attribute("IsEnabled")?.Value);
        Assert.Equal(
            "OnClearLoginWithDeviceQuarantineClick",
            quarantineClear.Attribute("Click")?.Value);
        Assert.Contains("LoginWithDeviceFingerprint", fingerprint.Attribute("Text")?.Value);
        Assert.Equal("True", fingerprint.Attribute("IsReadOnly")?.Value);
        Assert.Contains("LoginWithDeviceExpiryText", expiry.Attribute("Text")?.Value);
        Assert.Contains("CancelLoginWithDeviceCommand", cancel.Attribute("Command")?.Value);
        Assert.Contains(
            "ShowLoginWithDeviceTwoFactorExpiry",
            twoFactorExpiry.Attribute("IsOpen")?.Value);
        Assert.Contains(
            "LoginWithDeviceExpiryText",
            twoFactorExpiry.Attribute("Message")?.Value);
        Assert.Contains("ShowPrimaryAction", primary.Attribute("Visibility")?.Value);
        Assert.DoesNotContain("RequestId", document.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("OnUnloaded", document.Root?.Attribute("Unloaded")?.Value);
    }

    [Fact]
    public void Source_UsesCancelableThreeSecondPollingAndRefitsForDeviceState()
    {
        var viewModel = LoadSource("src", "App", "ViewModels", "LoginViewModel.cs");
        var page = LoadSource("src", "App", "Views", "LoginPage.xaml.cs");

        Assert.Contains("TimeSpan.FromSeconds(3)", viewModel);
        Assert.Contains(
            "Task.Delay(LoginWithDevicePollInterval, cancellation.Token)",
            viewModel);
        Assert.Contains("Volatile.Read(ref _loginWithDeviceGeneration)", viewModel);
        Assert.Contains("ReleaseDiscardedAuthResultAsync", viewModel);
        Assert.Contains("StartLoginWithDeviceTwoFactorExpiration", viewModel);
        Assert.Contains("nameof(LoginViewModel.LoginWithDeviceExpiryText)", page);
        Assert.Contains("nameof(LoginViewModel.ShowLoginWithDeviceOption)", page);
        Assert.Contains("DialogHelper.ConfirmAsync", page);
        Assert.DoesNotContain(".ShowAsync(", page, StringComparison.Ordinal);
        Assert.Contains("ClearLoginWithDeviceQuarantineCommand.ExecuteAsync", page);
        Assert.Contains(
            "nameof(LoginViewModel.ShowLoginWithDeviceQuarantineNotice)",
            page);
        Assert.Contains("SetSuccessHandler", page);
        Assert.Contains("ViewModel.Deactivate()", page);
        Assert.Contains("if (_isDeactivated)", page);
    }

    private static async Task<LoginViewModel> CreatePasswordStageAsync(
        FakeAuthService auth)
    {
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = ServerUrl,
            Email = Email,
        };
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.True(vm.IsPasswordStage);
        return vm;
    }

    private static FakeAuthService DeviceReadyAuth(
        DateTimeOffset expiresAt,
        Func<AuthenticationAttemptLease, CancellationToken, Task<AuthResult>> poll) =>
        new()
        {
            IsAvailable = true,
            BeginResult = new AuthResult.LoginWithDeviceWaiting(
                RequestId,
                Fingerprint,
                expiresAt),
            PollHandler = poll,
        };

    private static AuthResult.LoginWithDevicePending Pending(
        DateTimeOffset expiresAt,
        LoginWithDeviceUiStatus status) => new(
        RequestId,
        Fingerprint,
        expiresAt,
        status);

    private static LoginWithDeviceQuarantineInfo.Found QuarantineFound(
        bool isClearEligible)
    {
        var now = DateTimeOffset.UtcNow;
        return new LoginWithDeviceQuarantineInfo.Found(
            Guid.Parse("cb1513c9-e16e-4a18-8286-ab4ec498f53b"),
            7,
            QuarantineRequestId,
            now.AddMinutes(-10),
            now.AddMinutes(-5),
            isClearEligible ? now.AddMinutes(-1) : now.AddMinutes(5),
            isClearEligible);
    }

    private static TaskCompletionSource<T> NewSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate())
            await Task.Delay(10, cancellation.Token);
    }

    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(element => element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name" && attribute.Value == name));

    private static XDocument LoadXaml(string file) =>
        XDocument.Load(FindRepositoryFile("src", "App", "Views", file));

    private static string LoadSource(params string[] segments) =>
        File.ReadAllText(FindRepositoryFile(segments));

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(segments[^1]);
    }

    private sealed class FakeAuthService : IAuthService
    {
        private readonly VaultSession _session = new();

        public bool IsAvailable { get; init; }
        public Task<bool>? AvailabilityTask { get; init; }
        public Exception? AvailabilityException { get; init; }
        public LoginWithDeviceQuarantineInfo QuarantineInfo { get; set; } =
            new LoginWithDeviceQuarantineInfo.Absent();
        public Task<LoginWithDeviceQuarantineInfo>? QuarantineTask { get; init; }
        public LoginWithDeviceQuarantineClearResult ClearResult { get; set; } =
            new(LoginWithDeviceQuarantineClearStatus.Unavailable);
        public AuthResult? BeginResult { get; init; }
        public Task<AuthResult>? BeginTask { get; init; }
        public Func<AuthenticationAttemptLease, CancellationToken, Task<AuthResult>>?
            PollHandler { get; init; }
        public AuthResult SubmitResult { get; set; } =
            new AuthResult.Failure("not configured");
        public Task<AuthResult>? SubmitTask { get; set; }
        public Exception? LockException { get; set; }
        public (string ServerUrl, string Email)? AvailabilityCall { get; private set; }
        public (string ServerUrl, string Email)? QuarantineReadCall { get; private set; }
        public (
            string ServerUrl,
            string Email,
            LoginWithDeviceQuarantineInfo.Found Expected)? ClearCall { get; private set; }
        public TaskCompletionSource PollStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BeginStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SubmitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LockStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PollCalls { get; private set; }
        public int AvailabilityCalls { get; private set; }
        public int QuarantineReadCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public int AbandonCalls { get; private set; }
        public int LockCalls { get; private set; }
        public int ConditionalLogoutCalls { get; private set; }
        public string? SubmittedTwoFactorCode { get; private set; }
        public CancellationToken LastSubmitCancellationToken { get; private set; }
        public VaultSessionLease? LastLockedLease { get; private set; }
        public VaultSessionLease? LastLoggedOutLease { get; private set; }
        public LoginWithDeviceCancellationStatus CancellationStatus { get; set; } =
            LoginWithDeviceCancellationStatus.Cancelled;

        public AuthenticationAttemptLease BeginAuthenticationAttempt() =>
            _session.BeginAuthenticationAttempt();

        public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt)
        {
            AbandonCalls++;
            _session.AbandonAuthenticationAttempt(attempt);
        }

        public Task<bool> IsLoginWithDeviceAvailableAsync(
            string serverUrl,
            string email,
            CancellationToken ct = default)
        {
            AvailabilityCalls++;
            AvailabilityCall = (serverUrl, email);
            if (AvailabilityException is not null)
                return Task.FromException<bool>(AvailabilityException);
            return AvailabilityTask ?? Task.FromResult(IsAvailable);
        }

        public Task<LoginWithDeviceQuarantineInfo> GetLoginWithDeviceQuarantineInfoAsync(
            string serverUrl,
            string email,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            QuarantineReadCalls++;
            QuarantineReadCall = (serverUrl, email);
            return QuarantineTask ?? Task.FromResult(QuarantineInfo);
        }

        public Task<LoginWithDeviceQuarantineClearResult> ClearLoginWithDeviceQuarantineAsync(
            string serverUrl,
            string email,
            LoginWithDeviceQuarantineInfo.Found expected,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ClearCalls++;
            ClearCall = (serverUrl, email, expected);
            if (ClearResult.IsCleared)
                QuarantineInfo = new LoginWithDeviceQuarantineInfo.Absent();
            return Task.FromResult(ClearResult);
        }

        public Task<AuthResult> BeginLoginWithDeviceAsync(
            AuthenticationAttemptLease attempt,
            string serverUrl,
            string email,
            CancellationToken ct = default)
        {
            BeginStarted.TrySetResult();
            return BeginTask ?? Task.FromResult(BeginResult
                ?? new AuthResult.Failure("not configured"));
        }

        public Task<AuthResult> PollLoginWithDeviceAsync(
            AuthenticationAttemptLease attempt,
            CancellationToken ct = default)
        {
            PollCalls++;
            PollStarted.TrySetResult();
            return TrackPotentialQuarantineAsync(
                PollHandler?.Invoke(attempt, ct)
                ?? Task.FromResult<AuthResult>(new AuthResult.Failure("not configured")));
        }

        public LoginWithDeviceCancellationResult CancelLoginWithDevice(
            AuthenticationAttemptLease attempt)
        {
            CancelCalls++;
            _session.AbandonAuthenticationAttempt(attempt);
            if (CancellationStatus == LoginWithDeviceCancellationStatus.OutcomeUncertain)
                QuarantineInfo = QuarantineFound(isClearEligible: false);
            return new LoginWithDeviceCancellationResult(CancellationStatus);
        }

        public Task<AuthResult> LoginAsync(
            AuthenticationAttemptLease attempt,
            string serverUrl,
            string email,
            string masterPassword,
            CancellationToken ct = default) =>
            Task.FromResult<AuthResult>(new AuthResult.Failure("not configured"));

        public Task<AuthResult> SubmitTwoFactorAsync(
            AuthenticationAttemptLease attempt,
            string code,
            int provider,
            CancellationToken ct = default,
            bool rememberDevice = true)
        {
            SubmittedTwoFactorCode = code;
            LastSubmitCancellationToken = ct;
            SubmitStarted.TrySetResult();
            return TrackPotentialQuarantineAsync(
                SubmitTask ?? Task.FromResult(SubmitResult));
        }

        private async Task<AuthResult> TrackPotentialQuarantineAsync(
            Task<AuthResult> task)
        {
            var result = await task;
            if (result is AuthResult.LoginWithDeviceOutcomeUncertain
                or AuthResult.Failure { Kind: AuthFailureKind.OutcomeUncertain })
            {
                QuarantineInfo = QuarantineFound(isClearEligible: false);
            }
            return result;
        }

        public Task<bool> ResendTwoFactorEmailAsync(
            AuthenticationAttemptLease attempt,
            CancellationToken ct = default) => Task.FromResult(false);

        public Task<AuthResult> UnlockAsync(
            AuthenticationAttemptLease attempt,
            string masterPassword,
            CancellationToken ct = default) =>
            Task.FromResult<AuthResult>(new AuthResult.Failure("not configured"));

        public Task<AuthResult> UnlockWithPinAsync(
            AuthenticationAttemptLease attempt,
            string pin,
            CancellationToken ct = default) =>
            Task.FromResult<AuthResult>(new AuthResult.Failure("not configured"));

        public Task<AuthResult> UnlockWithHelloAsync(
            AuthenticationAttemptLease attempt,
            byte[] signature,
            CancellationToken ct = default) =>
            Task.FromResult<AuthResult>(new AuthResult.Failure("not configured"));

        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task LockAsync(
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        {
            LockCalls++;
            LastLockedLease = expectedSession;
            LockStarted.TrySetResult();
            return LockException is null
                ? Task.CompletedTask
                : Task.FromException(LockException);
        }

        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> LogoutAsync(
            VaultSessionLease expectedSession,
            CancellationToken ct = default)
        {
            ConditionalLogoutCalls++;
            LastLoggedOutLease = expectedSession;
            return Task.FromResult(true);
        }
    }
}
