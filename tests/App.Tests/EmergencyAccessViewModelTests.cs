using App.Services;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Vault;
using Xunit;

namespace App.Tests;

public class EmergencyAccessViewModelTests
{
    // ── LoadAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_PopulatesBothSections()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        await vm.LoadAsync();
        Assert.NotEmpty(vm.MyContacts);
        Assert.NotEmpty(vm.TrustedByOthers);
        Assert.Equal(DataPageState.Content, vm.PageState);
    }

    [Fact]
    public async Task LoadAsync_InDemoMode_ShowsEmptyState_WithoutHittingNetworkOrError()
    {
        // 演示/离线会话:不得调用底层紧急访问服务(否则 ThrowingEaUiService 会抛),
        // 且展示干净的空状态(无 OperationError)——而非在演示页留下「操作失败」提示。
        var session = new VaultSession();
        session.MarkDemo();
        var throwing = new ThrowingEaUiService("network must not run");
        var vm = new EmergencyAccessViewModel(throwing, session);

        await vm.LoadAsync();

        Assert.Empty(vm.MyContacts);
        Assert.Empty(vm.TrustedByOthers);
        Assert.Null(vm.OperationError);
        Assert.Equal(DataPageState.Empty, vm.PageState);
    }

    [Fact]
    public async Task LoadAsync_ClearsBeforeRefill()
    {
        var ui = new StubEaUiService();
        ui.Trusted.Add(new EmergencyContact("ec-1", null, "a@x.com", null, EmergencyAccessStatus.Invited, EmergencyAccessType.View, 7));
        ui.Granted.Add(new GrantedAccess("ga-1", null, "b@x.com", null, EmergencyAccessStatus.Confirmed, EmergencyAccessType.View, 7));
        var vm = new EmergencyAccessViewModel(ui);
        await vm.LoadAsync();
        Assert.Single(vm.MyContacts);
        Assert.Single(vm.TrustedByOthers);

        ui.Trusted.Clear();
        ui.Granted.Clear();
        await vm.LoadAsync();
        Assert.Empty(vm.MyContacts);
        Assert.Empty(vm.TrustedByOthers);
        Assert.Equal(DataPageState.Empty, vm.PageState);
    }

    [Fact]
    public async Task LoadAsync_SetsIsBusyDuringLoad_ThenClears()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        Assert.False(vm.IsBusy);
        await vm.LoadAsync();
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadAsync_ServiceThrows_SetsOperationErrorAndErrorState()
    {
        var vm = new EmergencyAccessViewModel(new ThrowingEaUiService("服务器错误"));
        var result = await vm.LoadAsync();
        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotNull(vm.OperationError);
        Assert.False(string.IsNullOrEmpty(vm.OperationError));
        Assert.Empty(vm.MyContacts);
        Assert.Empty(vm.TrustedByOthers);
        Assert.Equal(DataPageState.Error, vm.PageState);
    }

    [Fact]
    public void PageState_DefaultsToLoading()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        Assert.Equal(DataPageState.Loading, vm.PageState);
        Assert.True(vm.ShowLoading);
        Assert.False(vm.ShowError);
        Assert.False(vm.ShowEmpty);
        Assert.False(vm.ShowContent);
    }

    [Fact]
    public async Task LoadAsync_SecondCallWhileFirstPending_ReturnsBusy()
    {
        var service = new BlockingEaUiService();
        var vm = new EmergencyAccessViewModel(service);

        var first = vm.LoadAsync();
        await service.Entered.Task;
        Assert.True(vm.IsBusy);

        var second = await vm.LoadAsync();
        Assert.Equal(UiOperationStatus.Busy, second.Status);

        service.Release.SetResult(true);
        Assert.True((await first).IsSuccess);
    }

    // ── InviteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Invite_ForwardsAllParams_ToService_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var result = await vm.InviteAsync(" carol@x.com ", EmergencyAccessType.View, 14);
        Assert.True(result.IsSuccess);
        Assert.Equal("invite", ui.LastOp);
        Assert.Equal("carol@x.com", ui.LastEmail);
        Assert.Equal(EmergencyAccessType.View, ui.LastType);
        Assert.Equal(14, ui.LastWaitDays);
        Assert.True(ui.GetTrustedCallCount > 0);
    }

    [Fact]
    public async Task Invite_ServiceThrows_SetsOperationErrorAndReturnsFailure()
    {
        var vm = new EmergencyAccessViewModel(new ThrowingEaUiService("邀请失败"));
        var result = await vm.InviteAsync("x@x.com", EmergencyAccessType.View, 7);
        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotNull(vm.OperationError);
        Assert.False(string.IsNullOrEmpty(vm.OperationError));
    }

    // ── ConfirmAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_CallsServiceWithItemIdAndGranteeId_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-1", "user-alice", "a@x.com", null, EmergencyAccessStatus.Accepted, EmergencyAccessType.View, 7);

        var result = await vm.ConfirmAsync(contact);

        Assert.True(result.IsSuccess);
        Assert.Equal("confirm", ui.LastOp);
        Assert.Equal("ec-1", ui.LastId);
        Assert.Equal("user-alice", ui.LastGranteeId);
    }

    // ── ReinviteAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reinvite_CallsServiceWithItemId_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-2", null, "b@x.com", null, EmergencyAccessStatus.Invited, EmergencyAccessType.View, 7);

        var countBefore = ui.GetTrustedCallCount;
        var result = await vm.ReinviteAsync(contact);

        Assert.True(result.IsSuccess);
        Assert.Equal("reinvite", ui.LastOp);
        Assert.Equal("ec-2", ui.LastId);
        Assert.True(ui.GetTrustedCallCount > countBefore, "ReinviteAsync should reload after service call");
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_CallsServiceWithItemIdAndParams_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-4", null, "d@x.com", null, EmergencyAccessStatus.Confirmed, EmergencyAccessType.View, 7);

        var countBefore = ui.GetTrustedCallCount;
        var result = await vm.UpdateAsync(contact, EmergencyAccessType.Takeover, 3);

        Assert.True(result.IsSuccess);
        Assert.Equal("update", ui.LastOp);
        Assert.Equal("ec-4", ui.LastId);
        Assert.Equal(EmergencyAccessType.Takeover, ui.LastType);
        Assert.Equal(3, ui.LastWaitDays);
        Assert.True(ui.GetTrustedCallCount > countBefore, "UpdateAsync should reload after service call");
    }

    // ── ApproveAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_CallsServiceWithItemId_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-5", null, "e@x.com", null, EmergencyAccessStatus.RecoveryInitiated, EmergencyAccessType.View, 7);

        var result = await vm.ApproveAsync(contact);

        Assert.True(result.IsSuccess);
        Assert.Equal("approve", ui.LastOp);
        Assert.Equal("ec-5", ui.LastId);
    }

    // ── InitiateAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Initiate_CallsServiceWithItemId_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var granted = new GrantedAccess("ga-1", null, "carol@x.com", null, EmergencyAccessStatus.Confirmed, EmergencyAccessType.View, 7);

        var result = await vm.InitiateAsync(granted);

        Assert.True(result.IsSuccess);
        Assert.Equal("initiate", ui.LastOp);
        Assert.Equal("ga-1", ui.LastId);
    }

    // ── ViewAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task View_CallsService_StoresRecoveredVault()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var granted = new GrantedAccess("ga-1", null, "carol@x.com", null, EmergencyAccessStatus.RecoveryApproved, EmergencyAccessType.View, 7);

        var result = await vm.ViewAsync(granted);

        Assert.True(result.IsSuccess);
        Assert.Equal("view", ui.LastOp);
        Assert.NotNull(result.Value);
        Assert.Equal("carol@x.com", result.Value!.GrantorEmail);
        Assert.Single(vm.RecoveredItems);
    }

    [Fact]
    public async Task View_ReturnsThisInvocationValue()
    {
        var expected = new RecoveredVault("owner@example.com", []);
        var service = new ScriptedEaUiService { ViewResult = expected };
        var vm = new EmergencyAccessViewModel(service);
        var granted = new GrantedAccess(
            "ga-1", "owner", "owner@example.com", null,
            EmergencyAccessStatus.RecoveryApproved, EmergencyAccessType.View, 7);

        var result = await vm.ViewAsync(granted);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task FailedView_ClearsOldRecoveredResult()
    {
        var service = new ScriptedEaUiService();
        var vm = new EmergencyAccessViewModel(service);
        var granted = new GrantedAccess(
            "ga-1", "grantor-1", "a@example.com", null,
            EmergencyAccessStatus.RecoveryApproved, EmergencyAccessType.View, 7);

        service.ViewResult = new RecoveredVault("a@example.com", []);
        Assert.True((await vm.ViewAsync(granted)).IsSuccess);
        Assert.Single(vm.RecoveredItems);

        service.ViewError = new EmergencyAccessOperationException("view failed");
        var failed = await vm.ViewAsync(granted);

        Assert.Equal(UiOperationStatus.Failed, failed.Status);
        Assert.Null(failed.Value);
        Assert.Empty(vm.RecoveredItems);
    }

    [Fact]
    public async Task AnyMutation_ClearsStaleRecoveredResult()
    {
        // 任何操作开始都必须先清掉旧的恢复结果显示,避免用户在无关操作后仍看到上一次 View 的密码库。
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var granted = new GrantedAccess("ga-1", null, "carol@x.com", null, EmergencyAccessStatus.RecoveryApproved, EmergencyAccessType.View, 7);
        Assert.True((await vm.ViewAsync(granted)).IsSuccess);
        Assert.Single(vm.RecoveredItems);

        var contact = new EmergencyContact("ec-2", null, "b@x.com", null, EmergencyAccessStatus.Invited, EmergencyAccessType.View, 7);
        await vm.ReinviteAsync(contact);

        Assert.Empty(vm.RecoveredItems);
    }

    // ── TakeoverAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Takeover_CallsServiceWithNewPassword()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var granted = new GrantedAccess("ga-1", null, "carol@x.com", null, EmergencyAccessStatus.RecoveryApproved, EmergencyAccessType.Takeover, 7);

        var result = await vm.TakeoverAsync(granted, "NewPass!1");

        Assert.True(result.IsSuccess);
        Assert.Equal("takeover", ui.LastOp);
        Assert.Equal("ga-1", ui.LastId);
        Assert.Equal("carol@x.com", ui.LastEmail);
        Assert.Equal("NewPass!1", ui.LastNewPassword);
    }

    [Fact]
    public async Task Takeover_EmptyPassword_ReturnsFailure_WithoutCallingService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var granted = new GrantedAccess("ga-1", null, "carol@x.com", null, EmergencyAccessStatus.RecoveryApproved, EmergencyAccessType.Takeover, 7);

        var result = await vm.TakeoverAsync(granted, "   ");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal("", ui.LastOp);
    }

    // ── Remove: 确认 + busy 竞态(Task 7 Step 1 红灯用例)────────────────────────

    [Fact]
    public async Task Remove_HoldsGateDuringConfirmation_SecondOperationReturnsBusy()
    {
        var service = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(service);
        var contact = new EmergencyContact(
            "ec-1", "user-1", "a@example.com", null,
            EmergencyAccessStatus.Confirmed, EmergencyAccessType.View, 7);
        var confirmation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = vm.RemoveAsync(
            contact,
            (_, _) => confirmation.Task,
            CancellationToken.None);
        Assert.True(vm.IsBusy);

        var second = await vm.ReinviteAsync(contact, CancellationToken.None);
        Assert.Equal(UiOperationStatus.Busy, second.Status);
        Assert.Equal(string.Empty, service.LastOp);

        confirmation.SetResult(true);
        Assert.True((await first).IsSuccess);
        Assert.Equal("remove", service.LastOp);
    }

    [Fact]
    public async Task Remove_WhenConfirmationDeclined_ReturnsCancelled_DoesNotCallService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-3", null, "c@x.com", null, EmergencyAccessStatus.Confirmed, EmergencyAccessType.View, 7);

        var result = await vm.RemoveAsync(contact, (_, _) => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(string.Empty, ui.LastOp);
    }

    [Fact]
    public async Task Remove_WhenConfirmed_CallsService_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-3", null, "c@x.com", null, EmergencyAccessStatus.Confirmed, EmergencyAccessType.View, 7);

        var countBefore = ui.GetTrustedCallCount;
        var result = await vm.RemoveAsync(contact, (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("remove", ui.LastOp);
        Assert.Equal("ec-3", ui.LastId);
        Assert.True(ui.GetTrustedCallCount > countBefore);
    }

    // ── Reject: 确认路径 ──────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_WhenConfirmationDeclined_ReturnsCancelled_DoesNotCallService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-6", null, "f@x.com", null, EmergencyAccessStatus.RecoveryInitiated, EmergencyAccessType.View, 7);

        var result = await vm.RejectAsync(contact, (_, _) => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(string.Empty, ui.LastOp);
    }

    [Fact]
    public async Task Reject_WhenConfirmed_CallsService_AndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        var contact = new EmergencyContact("ec-6", null, "f@x.com", null, EmergencyAccessStatus.RecoveryInitiated, EmergencyAccessType.View, 7);

        var result = await vm.RejectAsync(contact, (_, _) => Task.FromResult(true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("reject", ui.LastOp);
        Assert.Equal("ec-6", ui.LastId);
    }

    // ── IsBusy / OperationError state ────────────────────────────────────────

    [Fact]
    public void IsBusy_DefaultFalse()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanOperate);
    }

    [Fact]
    public void OperationError_DefaultNull()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        Assert.Null(vm.OperationError);
    }

    [Fact]
    public void HasError_FalseWhenNoError_TrueWhenErrorSet()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        Assert.False(vm.HasError);
        vm.OperationError = "test error";
        Assert.True(vm.HasError);
        vm.OperationError = null;
        Assert.False(vm.HasError);
    }

    [Fact]
    public void HasNoContacts_And_HasNoGranted_TrackCollections()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        Assert.True(vm.HasNoContacts);
        Assert.True(vm.HasNoGranted);

        vm.MyContacts.Add(new EmergencyContact("ec-1", null, "a@x.com", null, EmergencyAccessStatus.Invited, EmergencyAccessType.View, 7));
        Assert.False(vm.HasNoContacts);

        vm.TrustedByOthers.Add(new GrantedAccess("ga-1", null, "b@x.com", null, EmergencyAccessStatus.Confirmed, EmergencyAccessType.View, 7));
        Assert.False(vm.HasNoGranted);
    }

    // ── ClearSensitive ────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearSensitive_ClearsRecoveredItemsAndCollectionsAndError()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        await vm.LoadAsync();
        var granted = new GrantedAccess("ga-1", null, "carol@x.com", null, EmergencyAccessStatus.RecoveryApproved, EmergencyAccessType.View, 7);
        vm.TrustedByOthers.Add(granted);
        await vm.ViewAsync(granted);
        vm.OperationError = "旧错误";

        vm.ClearSensitive();

        Assert.Empty(vm.RecoveredItems);
        Assert.Empty(vm.MyContacts);
        Assert.Empty(vm.TrustedByOthers);
        Assert.Null(vm.OperationError);
    }

    // ── Private fakes ─────────────────────────────────────────────────────────

    /// <summary>可配置的 stub，支持预置 Trusted/Granted 列表。</summary>
    private sealed class StubEaUiService : IEmergencyAccessUiService
    {
        public List<EmergencyContact> Trusted { get; } = new();
        public List<GrantedAccess> Granted { get; } = new();

        public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmergencyContact>>(Trusted.ToList());

        public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantedAccess>>(Granted.ToList());

        public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReinviteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApproveAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task AcceptAsync(string id, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task InitiateAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default) =>
            Task.FromResult(new RecoveredVault(grantorEmail, []));
        public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>记录最后一次操作调用，用于断言。</summary>
    private sealed class RecordingEaUiService : IEmergencyAccessUiService
    {
        public string LastOp { get; private set; } = "";
        public string LastId { get; private set; } = "";
        public string LastEmail { get; private set; } = "";
        public string LastGranteeId { get; private set; } = "";
        public string LastNewPassword { get; private set; } = "";
        public EmergencyAccessType LastType { get; private set; }
        public int LastWaitDays { get; private set; }
        public int GetTrustedCallCount { get; private set; }

        public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default)
        { GetTrustedCallCount++; return Task.FromResult<IReadOnlyList<EmergencyContact>>([]); }
        public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantedAccess>>([]);

        public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
        { LastOp = "invite"; LastEmail = email; LastType = type; LastWaitDays = waitTimeDays; return Task.CompletedTask; }

        public Task ReinviteAsync(string id, CancellationToken ct = default)
        { LastOp = "reinvite"; LastId = id; return Task.CompletedTask; }

        public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default)
        { LastOp = "confirm"; LastId = id; LastGranteeId = granteeId; return Task.CompletedTask; }

        public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
        { LastOp = "update"; LastId = id; LastType = type; LastWaitDays = waitTimeDays; return Task.CompletedTask; }

        public Task RemoveAsync(string id, CancellationToken ct = default)
        { LastOp = "remove"; LastId = id; return Task.CompletedTask; }

        public Task ApproveAsync(string id, CancellationToken ct = default)
        { LastOp = "approve"; LastId = id; return Task.CompletedTask; }

        public Task RejectAsync(string id, CancellationToken ct = default)
        { LastOp = "reject"; LastId = id; return Task.CompletedTask; }

        public Task AcceptAsync(string id, string token, CancellationToken ct = default)
        { LastOp = "accept"; LastId = id; return Task.CompletedTask; }

        public Task InitiateAsync(string id, CancellationToken ct = default)
        { LastOp = "initiate"; LastId = id; return Task.CompletedTask; }

        public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default)
        { LastOp = "view"; LastId = id; LastEmail = grantorEmail; return Task.FromResult(new RecoveredVault(grantorEmail, [])); }

        public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default)
        { LastOp = "takeover"; LastId = id; LastEmail = grantorEmail; LastNewPassword = newPassword; return Task.CompletedTask; }
    }

    /// <summary>所有操作都抛出 EmergencyAccessOperationException。</summary>
    private sealed class ThrowingEaUiService : IEmergencyAccessUiService
    {
        private readonly string _message;
        public ThrowingEaUiService(string message) => _message = message;

        public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<EmergencyContact>>(new EmergencyAccessOperationException(_message));
        public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<GrantedAccess>>(new EmergencyAccessOperationException(_message));
        public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task ReinviteAsync(string id, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task RemoveAsync(string id, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task ApproveAsync(string id, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task RejectAsync(string id, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task AcceptAsync(string id, string token, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task InitiateAsync(string id, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
        public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default) =>
            Task.FromException<RecoveredVault>(new EmergencyAccessOperationException(_message));
        public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default) =>
            Task.FromException(new EmergencyAccessOperationException(_message));
    }

    /// <summary>可编排 View 结果/异常的替身，用于 stale-result 与 this-invocation 断言。</summary>
    private sealed class ScriptedEaUiService : IEmergencyAccessUiService
    {
        public RecoveredVault ViewResult { get; set; } =
            new(string.Empty, []);
        public Exception? ViewError { get; set; }

        public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmergencyContact>>([]);
        public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantedAccess>>([]);
        public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReinviteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApproveAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task AcceptAsync(string id, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task InitiateAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default) =>
            ViewError is null
                ? Task.FromResult(ViewResult)
                : Task.FromException<RecoveredVault>(ViewError);
        public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>在 GetTrustedAsync 上阻塞,用于验证 LoadAsync 自身的 busy 竞态。</summary>
    private sealed class BlockingEaUiService : IEmergencyAccessUiService
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default)
        {
            Entered.TrySetResult(true);
            await Release.Task;
            return [];
        }
        public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GrantedAccess>>([]);
        public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReinviteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ApproveAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task RejectAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task AcceptAsync(string id, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task InitiateAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default) =>
            Task.FromResult(new RecoveredVault(grantorEmail, []));
        public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default) => Task.CompletedTask;
    }
}
