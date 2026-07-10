using App.Services;
using Core.Services;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class ItemRepromptGateTests
{
    [Fact]
    public async Task AuthorizingA_DoesNotAuthorizeB_AndGenerationChangeRevokesA()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new Core.Models.ServerOrigin("https://vault.example"));
        var account = new AccountVerifier("correct");
        var gate = new ItemRepromptGate(account, session);
        var a = new CipherAccessPolicy("a", true);
        var b = new CipherAccessPolicy("b", true);

        var result = await gate.AuthorizeAsync(
            a,
            SensitiveItemAction.Copy,
            _ => Task.FromResult<string?>("correct"));

        Assert.True(result.IsSuccess);
        Assert.True(gate.IsAuthorized(a));
        Assert.False(gate.IsAuthorized(b));

        session.Lock();

        Assert.False(gate.IsAuthorized(a));
    }

    [Fact]
    public async Task CancelOrWrongPassword_NeverCachesAuthorization()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new Core.Models.ServerOrigin("https://vault.example"));
        var gate = new ItemRepromptGate(new AccountVerifier("correct"), session);
        var policy = new CipherAccessPolicy("a", true);

        var cancelled = await gate.AuthorizeAsync(
            policy,
            SensitiveItemAction.Edit,
            _ => Task.FromResult<string?>(null));
        var denied = await gate.AuthorizeAsync(
            policy,
            SensitiveItemAction.Edit,
            _ => Task.FromResult<string?>("wrong"));

        Assert.Equal(UiOperationStatus.Cancelled, cancelled.Status);
        Assert.Equal(UiOperationStatus.Failed, denied.Status);
        Assert.False(gate.IsAuthorized(policy));
    }

    private sealed class AccountVerifier(string accepted) : IAccountService
    {
        public Task<bool> VerifyMasterPasswordAsync(string password, CancellationToken ct = default) =>
            Task.FromResult(password == accepted);
        public Task UpdateNameAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
