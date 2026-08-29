using Api;
using App.Services;
using Core.Enums;
using Core.Models;
using Core.Services;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class ItemRepromptGateTests
{
    /// <summary>
    /// 饱和 generation（long.MaxValue）场景：generation 无法再递增，但每次 Lock
    /// 仍轮换 stamp 与 CTS。缓存键必须包含 lease（含 stamp），仅比较 generation+account
    /// 则在此态下无法失效授权。
    /// </summary>
    [Fact]
    public async Task Lock_AtSaturatedGeneration_RevokesAuthorization()
    {
        var session = new VaultSession();
        var origin = new ServerOrigin("https://vault.example");

        // 通过 RestorePersistedSession 将 generation 设到 long.MaxValue-1，再经 Lock 推进到 long.MaxValue
        var persisted = new PersistedSession(
            SchemaVersion: PersistedSession.CurrentSchemaVersion,
            AccountId: "account-sat",
            Origin: origin,
            Generation: long.MaxValue - 1,
            Email: "sat@example.com",
            DeviceIdentifier: "device-1",
            RefreshToken: "token",
            ProtectedUserKey: "key",
            KdfType: KdfType.Pbkdf2,
            KdfIterations: 600_000,
            KdfMemory: null,
            KdfParallelism: null);
        session.RestorePersistedSession(persisted);
        Assert.Equal(long.MaxValue - 1, session.Generation);

        // Lock → generation 饱和为 long.MaxValue，stamp = S1
        session.Lock();
        Assert.Equal(long.MaxValue, session.Generation);

        var gate = new ItemRepromptGate(new AccountVerifier("correct"), session);
        var policy = new CipherAccessPolicy("cipher-sat", true);

        // 授权（session 在 Locked 状态但仍有活跃租约）
        var result = await gate.AuthorizeAsync(
            policy,
            SensitiveItemAction.Copy,
            _ => Task.FromResult<string?>("correct"));

        Assert.True(result.IsSuccess);
        Assert.True(gate.IsAuthorized(policy));

        // 再次 Lock：generation 保持 long.MaxValue，但 stamp 变为 S2
        session.Lock();
        Assert.Equal(long.MaxValue, session.Generation);

        // 关键断言：仅比较 generation+account 的实现在此会错误地返回 true
        Assert.False(gate.IsAuthorized(policy));
    }

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

    [Fact]
    public async Task RequestTimeout_ReturnsFailureInsteadOfCancelled()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://vault.example"));
        var gate = new ItemRepromptGate(new TimeoutAccountVerifier(), session);
        var policy = new CipherAccessPolicy("a", true);

        var result = await gate.AuthorizeAsync(
            policy,
            SensitiveItemAction.Edit,
            _ => Task.FromResult<string?>("correct"),
            CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, result.Error);
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

    private sealed class TimeoutAccountVerifier : IAccountService
    {
        public Task<bool> VerifyMasterPasswordAsync(
            string password,
            CancellationToken ct = default) => Task.FromException<bool>(new TaskCanceledException(
                "request timed out",
                new RequestTimeoutException(
                    RequestTimeoutPhase.ControlPlane,
                    TimeSpan.FromSeconds(30)),
                CancellationToken.None));

        public Task UpdateNameAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ChangePasswordAsync(
            string currentPassword,
            string newPassword,
            string? hint,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangeKdfAsync(
            string currentPassword,
            int newIterations,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
