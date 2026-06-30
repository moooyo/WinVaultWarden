using App.Services;
using App.ViewModels;
using Core.Models;
using Core.Services;
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
    public async Task LoadAsync_ServiceThrows_SetsOperationError()
    {
        var vm = new EmergencyAccessViewModel(new ThrowingEaUiService("服务器错误"));
        await vm.LoadAsync();
        Assert.NotNull(vm.OperationError);
        Assert.False(string.IsNullOrEmpty(vm.OperationError));
        Assert.Empty(vm.MyContacts);
        Assert.Empty(vm.TrustedByOthers);
    }

    // ── InviteCommand ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Invite_CallsServiceAndReloads()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.InviteEmail = "b@x.com";
        vm.InviteType = EmergencyAccessType.Takeover;
        vm.InviteWaitTimeDays = 7;
        await vm.InviteCommand.ExecuteAsync(null);
        Assert.Equal("invite", ui.LastOp);
    }

    [Fact]
    public async Task Invite_ForwardsAllParams_ToService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.InviteEmail = "carol@x.com";
        vm.InviteType = EmergencyAccessType.View;
        vm.InviteWaitTimeDays = 14;
        await vm.InviteCommand.ExecuteAsync(null);
        Assert.Equal("carol@x.com", ui.LastEmail);
        Assert.Equal(EmergencyAccessType.View, ui.LastType);
        Assert.Equal(14, ui.LastWaitDays);
    }

    [Fact]
    public async Task Invite_ServiceThrows_SetsOperationError()
    {
        var vm = new EmergencyAccessViewModel(new ThrowingEaUiService("邀请失败"));
        vm.InviteEmail = "x@x.com";
        vm.InviteType = EmergencyAccessType.View;
        vm.InviteWaitTimeDays = 7;
        await vm.InviteCommand.ExecuteAsync(null);
        Assert.NotNull(vm.OperationError);
        Assert.False(string.IsNullOrEmpty(vm.OperationError));
    }

    // ── ConfirmCommand ────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_CallsService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedContactId = "ec-1";
        vm.SelectedGranteeId = "user-alice";
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("confirm", ui.LastOp);
        Assert.Equal("ec-1", ui.LastId);
        Assert.Equal("user-alice", ui.LastGranteeId);
    }

    // ── ReinviteCommand ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reinvite_CallsService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedContactId = "ec-2";
        await vm.ReinviteCommand.ExecuteAsync(null);
        Assert.Equal("reinvite", ui.LastOp);
        Assert.Equal("ec-2", ui.LastId);
    }

    // ── RemoveCommand ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_CallsService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedContactId = "ec-3";
        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.Equal("remove", ui.LastOp);
        Assert.Equal("ec-3", ui.LastId);
    }

    // ── UpdateCommand ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_CallsService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedContactId = "ec-4";
        vm.InviteType = EmergencyAccessType.Takeover;
        vm.InviteWaitTimeDays = 3;
        await vm.UpdateCommand.ExecuteAsync(null);
        Assert.Equal("update", ui.LastOp);
        Assert.Equal("ec-4", ui.LastId);
    }

    // ── ApproveCommand ────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_CallsService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedContactId = "ec-5";
        await vm.ApproveCommand.ExecuteAsync(null);
        Assert.Equal("approve", ui.LastOp);
        Assert.Equal("ec-5", ui.LastId);
    }

    // ── RejectCommand ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_CallsService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedContactId = "ec-6";
        await vm.RejectCommand.ExecuteAsync(null);
        Assert.Equal("reject", ui.LastOp);
        Assert.Equal("ec-6", ui.LastId);
    }

    // ── InitiateCommand ───────────────────────────────────────────────────────

    [Fact]
    public async Task Initiate_CallsService()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedGrantedId = "ga-1";
        await vm.InitiateCommand.ExecuteAsync(null);
        Assert.Equal("initiate", ui.LastOp);
        Assert.Equal("ga-1", ui.LastId);
    }

    // ── ViewCommand ───────────────────────────────────────────────────────────

    [Fact]
    public async Task View_CallsService_StoresRecoveredVault()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedGrantedId = "ga-1";
        vm.SelectedGrantorEmail = "carol@x.com";
        await vm.ViewCommand.ExecuteAsync(null);
        Assert.Equal("view", ui.LastOp);
        Assert.NotNull(vm.RecoveredVault);
        Assert.Equal("carol@x.com", vm.RecoveredVault!.GrantorEmail);
    }

    // ── TakeoverCommand ───────────────────────────────────────────────────────

    [Fact]
    public async Task Takeover_CallsServiceWithNewPassword()
    {
        var ui = new RecordingEaUiService();
        var vm = new EmergencyAccessViewModel(ui);
        vm.SelectedGrantedId = "ga-1";
        vm.SelectedGrantorEmail = "carol@x.com";
        vm.TakeoverNewPassword = "NewPass!1";
        await vm.TakeoverCommand.ExecuteAsync(null);
        Assert.Equal("takeover", ui.LastOp);
        Assert.Equal("ga-1", ui.LastId);
        Assert.Equal("carol@x.com", ui.LastEmail);
        Assert.Equal("NewPass!1", ui.LastNewPassword);
    }

    // ── IsBusy / OperationError state ────────────────────────────────────────

    [Fact]
    public void IsBusy_DefaultFalse()
    {
        var vm = new EmergencyAccessViewModel(new MockEmergencyAccessUiService());
        Assert.False(vm.IsBusy);
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

        public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EmergencyContact>>([]);
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
}
