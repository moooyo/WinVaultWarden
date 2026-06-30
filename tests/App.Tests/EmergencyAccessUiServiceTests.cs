using App.Services;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public class EmergencyAccessUiServiceTests
{
    // ── 转发测试 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invite_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.InviteAsync("b@x.com", EmergencyAccessType.Takeover, 7);
        Assert.Equal(("invite", "b@x.com", 1, 7),
            (core.LastOp, core.LastEmail, (int)core.LastType, core.LastWait));
    }

    [Fact]
    public async Task Reinvite_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.ReinviteAsync("id-1");
        Assert.Equal(("reinvite", "id-1"), (core.LastOp, core.LastId));
    }

    [Fact]
    public async Task Confirm_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.ConfirmAsync("id-2", "granteeId-2");
        Assert.Equal(("confirm", "id-2", "granteeId-2"), (core.LastOp, core.LastId, core.LastGranteeId));
    }

    [Fact]
    public async Task Update_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.UpdateAsync("id-3", EmergencyAccessType.View, 14);
        Assert.Equal(("update", "id-3", 0, 14), (core.LastOp, core.LastId, (int)core.LastType, core.LastWait));
    }

    [Fact]
    public async Task Remove_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.RemoveAsync("id-4");
        Assert.Equal(("remove", "id-4"), (core.LastOp, core.LastId));
    }

    [Fact]
    public async Task Approve_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.ApproveAsync("id-5");
        Assert.Equal(("approve", "id-5"), (core.LastOp, core.LastId));
    }

    [Fact]
    public async Task Reject_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.RejectAsync("id-6");
        Assert.Equal(("reject", "id-6"), (core.LastOp, core.LastId));
    }

    [Fact]
    public async Task GetTrusted_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.GetTrustedAsync();
        Assert.Equal("getTrusted", core.LastOp);
    }

    [Fact]
    public async Task GetGranted_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.GetGrantedAsync();
        Assert.Equal("getGranted", core.LastOp);
    }

    [Fact]
    public async Task Accept_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.AcceptAsync("id-7", "token-abc");
        Assert.Equal(("accept", "id-7", "token-abc"), (core.LastOp, core.LastId, core.LastToken));
    }

    [Fact]
    public async Task Initiate_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.InitiateAsync("id-8");
        Assert.Equal(("initiate", "id-8"), (core.LastOp, core.LastId));
    }

    [Fact]
    public async Task View_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.ViewAsync("id-9", "grantor@x.com");
        Assert.Equal(("view", "id-9", "grantor@x.com"), (core.LastOp, core.LastId, core.LastEmail));
    }

    [Fact]
    public async Task TakeoverAndResetPassword_ForwardsToCoreService()
    {
        var core = new RecordingEaService();
        var ui = new EmergencyAccessUiService(core);
        await ui.TakeoverAndResetPasswordAsync("id-10", "grantor@x.com", "NewPass!1");
        Assert.Equal(("takeover", "id-10", "grantor@x.com", "NewPass!1"),
            (core.LastOp, core.LastId, core.LastEmail, core.LastNewPassword));
    }

    // ── Mock 冒烟测试 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task MockGetTrustedAsync_ReturnsNonEmptyList()
    {
        var mock = new MockEmergencyAccessUiService();
        var contacts = await mock.GetTrustedAsync();
        Assert.NotEmpty(contacts);
    }

    [Fact]
    public async Task MockGetGrantedAsync_ReturnsNonEmptyList()
    {
        var mock = new MockEmergencyAccessUiService();
        var granted = await mock.GetGrantedAsync();
        Assert.NotEmpty(granted);
    }

    [Fact]
    public async Task MockViewAsync_ReturnsSampleVault()
    {
        var mock = new MockEmergencyAccessUiService();
        var vault = await mock.ViewAsync("any-id", "grantor@x.com");
        Assert.NotNull(vault);
        Assert.False(string.IsNullOrWhiteSpace(vault.GrantorEmail));
    }

    // ── RecordingEaService ────────────────────────────────────────────────────

    private sealed class RecordingEaService : IEmergencyAccessService
    {
        public string LastOp { get; private set; } = "";
        public string LastId { get; private set; } = "";
        public string LastEmail { get; private set; } = "";
        public string LastGranteeId { get; private set; } = "";
        public string LastToken { get; private set; } = "";
        public string LastNewPassword { get; private set; } = "";
        public EmergencyAccessType LastType { get; private set; }
        public int LastWait { get; private set; }

        public Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default)
        { LastOp = "getTrusted"; return Task.FromResult<IReadOnlyList<EmergencyContact>>([]); }

        public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
        { LastOp = "invite"; LastEmail = email; LastType = type; LastWait = waitTimeDays; return Task.CompletedTask; }

        public Task ReinviteAsync(string id, CancellationToken ct = default)
        { LastOp = "reinvite"; LastId = id; return Task.CompletedTask; }

        public Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default)
        { LastOp = "confirm"; LastId = id; LastGranteeId = granteeId; return Task.CompletedTask; }

        public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
        { LastOp = "update"; LastId = id; LastType = type; LastWait = waitTimeDays; return Task.CompletedTask; }

        public Task RemoveAsync(string id, CancellationToken ct = default)
        { LastOp = "remove"; LastId = id; return Task.CompletedTask; }

        public Task ApproveAsync(string id, CancellationToken ct = default)
        { LastOp = "approve"; LastId = id; return Task.CompletedTask; }

        public Task RejectAsync(string id, CancellationToken ct = default)
        { LastOp = "reject"; LastId = id; return Task.CompletedTask; }

        public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default)
        { LastOp = "getGranted"; return Task.FromResult<IReadOnlyList<GrantedAccess>>([]); }

        public Task AcceptAsync(string id, string token, CancellationToken ct = default)
        { LastOp = "accept"; LastId = id; LastToken = token; return Task.CompletedTask; }

        public Task InitiateAsync(string id, CancellationToken ct = default)
        { LastOp = "initiate"; LastId = id; return Task.CompletedTask; }

        public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default)
        { LastOp = "view"; LastId = id; LastEmail = grantorEmail; return Task.FromResult(new RecoveredVault(grantorEmail, [])); }

        public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default)
        { LastOp = "takeover"; LastId = id; LastEmail = grantorEmail; LastNewPassword = newPassword; return Task.CompletedTask; }
    }
}
