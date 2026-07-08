using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Services;
using Vault;
using Xunit;

namespace App.Tests;

public class MasterPasswordRepromptTests
{
    private static LoginDetail Reprompt(bool on) =>
        new() { Id = "x", Name = "n", Reprompt = on };

    // ── VaultViewModel 决策方法 ───────────────────────────────────────────────

    [Fact]
    public void RequiresReprompt_true_for_reprompt_cipher_when_unverified()
    {
        var vm = new VaultViewModel(new MockVaultUiService(), reprompt: new FakeReprompt());
        Assert.True(vm.RequiresReprompt(Reprompt(true)));
    }

    [Fact]
    public void RequiresReprompt_false_when_session_already_verified()
    {
        var vm = new VaultViewModel(new MockVaultUiService(), reprompt: new FakeReprompt { Verified = true });
        Assert.False(vm.RequiresReprompt(Reprompt(true)));
    }

    [Fact]
    public void RequiresReprompt_false_for_non_reprompt_cipher()
    {
        var vm = new VaultViewModel(new MockVaultUiService(), reprompt: new FakeReprompt());
        Assert.False(vm.RequiresReprompt(Reprompt(false)));
    }

    [Fact]
    public void RequiresReprompt_false_when_no_gate_service()
    {
        var vm = new VaultViewModel(new MockVaultUiService()); // 无门禁 → 特性禁用,保持旧行为
        Assert.False(vm.RequiresReprompt(Reprompt(true)));
    }

    [Fact]
    public async Task VerifyRepromptAsync_gates_until_correct_password()
    {
        var vm = new VaultViewModel(new MockVaultUiService(), reprompt: new FakeReprompt());
        var detail = Reprompt(true);

        Assert.True(vm.RequiresReprompt(detail));
        Assert.False(await vm.VerifyRepromptAsync("wrong"));
        Assert.True(vm.RequiresReprompt(detail));            // 仍需要
        Assert.True(await vm.VerifyRepromptAsync("correct"));
        Assert.False(vm.RequiresReprompt(detail));           // 验证后本会话不再要求
    }

    [Fact]
    public async Task VerifyRepromptAsync_returns_true_when_no_gate_service()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        Assert.True(await vm.VerifyRepromptAsync("anything")); // 无门禁不阻断
    }

    // ── 具体门禁服务:仅成功时置会话标志,Lock 复位 ────────────────────────────

    [Fact]
    public async Task MasterPasswordReprompt_marks_session_only_on_success_and_resets_on_lock()
    {
        var session = new VaultSession();
        var gate = new MasterPasswordReprompt(new StubAccount(accepts: "right"), session);

        Assert.False(gate.IsSessionVerified);
        Assert.False(await gate.VerifyAsync("wrong"));
        Assert.False(gate.IsSessionVerified);
        Assert.True(await gate.VerifyAsync("right"));
        Assert.True(gate.IsSessionVerified);

        session.Lock();
        Assert.False(gate.IsSessionVerified); // E1b:锁定复位
    }

    // ── 测试替身 ──────────────────────────────────────────────────────────────

    private sealed class FakeReprompt : IMasterPasswordReprompt
    {
        public bool Verified { get; set; }
        public bool IsSessionVerified => Verified;
        public Task<bool> VerifyAsync(string password, CancellationToken ct = default)
        {
            if (password == "correct") Verified = true;
            return Task.FromResult(Verified);
        }
    }

    private sealed class StubAccount : IAccountService
    {
        private readonly string _accepts;
        public StubAccount(string accepts) => _accepts = accepts;
        public Task<bool> VerifyMasterPasswordAsync(string password, CancellationToken ct = default)
            => Task.FromResult(password == _accepts);
        public Task UpdateNameAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
