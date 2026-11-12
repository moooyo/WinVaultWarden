using App.Services;
using App.ViewModels;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class AccountManagementViewModelTests
{
    [Fact]
    public async Task EmailChangeRequiresTheChallengeForTheSameNormalizedAddress()
    {
        using var fixture = new Fixture();
        Assert.False((await fixture.ViewModel.ChangeEmailAsync("password", "next@example.com", "code")).IsSuccess);
        Assert.Equal(0, fixture.Management.ChangeCalls);
        Assert.True((await fixture.ViewModel.RequestEmailChangeAsync("password", " Next@Example.com ")).IsSuccess);
        Assert.True(fixture.ViewModel.EmailChangeCodeSent);
        Assert.False((await fixture.ViewModel.ChangeEmailAsync("password", "other@example.com", "code")).IsSuccess);
        Assert.Equal(0, fixture.Management.ChangeCalls);
        Assert.True((await fixture.ViewModel.ChangeEmailAsync("password", "next@example.com", "code")).IsSuccess);
        Assert.Equal(1, fixture.Management.ChangeCalls);
    }

    [Fact]
    public async Task ClearingSensitiveStateInvalidatesAChallengeAndCancelsLateFeedback()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Management.RequestAction = _ => pending.Task;
        var operation = fixture.ViewModel.RequestEmailChangeAsync("password", "next@example.com");
        Assert.True(fixture.ViewModel.IsBusy);
        fixture.ViewModel.ClearSensitive();
        pending.SetResult();
        var result = await operation;
        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.False(fixture.ViewModel.EmailChangeCodeSent);
        Assert.Null(fixture.ViewModel.Feedback);
    }

    [Fact]
    public async Task ConcurrentOperationsAreRejectedWithoutDispatch()
    {
        using var fixture = new Fixture();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Management.RequestAction = _ => pending.Task;
        var first = fixture.ViewModel.RequestEmailChangeAsync("password", "next@example.com");
        var second = await fixture.ViewModel.RequestEmailVerificationAsync();
        Assert.Equal(UiOperationStatus.Busy, second.Status);
        Assert.Equal(0, fixture.Management.VerificationRequests);
        pending.SetResult();
        await first;
    }

    [Fact]
    public async Task VerificationSuccessIsBackedByTheService()
    {
        using var fixture = new Fixture();
        Assert.True((await fixture.ViewModel.RequestEmailVerificationAsync()).IsSuccess);
        Assert.Equal(1, fixture.Management.VerificationRequests);
        Assert.True((await fixture.ViewModel.VerifyEmailAsync("token")).IsSuccess);
        Assert.Equal("token", fixture.Management.LastToken);
        LocalizedSourceAssert.ContainsEnglishDisplay("server confirmed", fixture.ViewModel.Feedback!);
    }

    [Fact]
    public async Task SecurityChangeCompletionUsesTheReceiptLease()
    {
        using var fixture = new Fixture();
        var original = fixture.Session.CaptureLease();
        var locked = fixture.Session.Lock();
        var receipt = new AccountSecurityChangeReceipt(AccountSecurityChangeOutcome.Uncertain,
            original, locked, true);
        await fixture.ViewModel.CompleteSecurityChangeAsync(receipt);
        Assert.Equal(AppLifecycleIntent.CredentialChanged, fixture.Lifecycle.Intent);
        Assert.Same(locked, fixture.Lifecycle.Expected);
    }

    [Fact]
    public void UnsupportedEmailChangesKeepVerificationAndRevocationAvailable()
    {
        using var fixture = new Fixture();
        fixture.Management.Availability = new(false, "V2 email changes are unsupported by this server.");
        fixture.ViewModel.RefreshAvailability();
        Assert.False(fixture.ViewModel.CanRequestEmailChange);
        Assert.True(fixture.ViewModel.CanOperate);
        Assert.Contains("V2", fixture.ViewModel.EmailChangeUnavailableReason!);
    }

    [Fact]
    public async Task LockedViewModelDoesNotDispatchAStaleButtonAction()
    {
        using var fixture = new Fixture();
        fixture.Session.Lock();
        var result = await fixture.ViewModel.RequestEmailVerificationAsync();
        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(0, fixture.Management.VerificationRequests);
    }

    private sealed class Fixture : IDisposable
    {
        public VaultSession Session { get; } = new();
        public ManagementStub Management { get; } = new();
        public LifecycleStub Lifecycle { get; } = new();
        public AccountManagementViewModel ViewModel { get; }
        public Fixture()
        {
            Session.BeginAccountSession("account", "current@example.com", new Core.Models.ServerOrigin("https://vault.example"));
            Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            ViewModel = new(Management, Session, Lifecycle);
            ViewModel.RefreshAvailability();
        }
        public void Dispose() => Session.Logout();
    }

    private sealed class ManagementStub : IAccountManagementService
    {
        public AccountManagementAvailability Availability { get; set; } = new(true, null);
        public Func<CancellationToken, Task> RequestAction { get; set; } = _ => Task.CompletedTask;
        public int ChangeCalls { get; private set; }
        public int VerificationRequests { get; private set; }
        public string? LastToken { get; private set; }
        public AccountManagementAvailability GetAvailability() => Availability;
        public Task RequestEmailChangeAsync(string currentPassword, string newEmail, CancellationToken ct = default) =>
            RequestAction(ct);
        public Task<AccountSecurityChangeReceipt> ChangeEmailAsync(
            string currentPassword, string newEmail, string token, CancellationToken ct = default)
        {
            ChangeCalls++;
            return Task.FromResult(new AccountSecurityChangeReceipt(
                AccountSecurityChangeOutcome.Confirmed, null!, null, true));
        }
        public Task RequestEmailVerificationAsync(CancellationToken ct = default)
        { VerificationRequests++; return Task.CompletedTask; }
        public Task VerifyEmailAsync(string token, CancellationToken ct = default)
        { LastToken = token; return Task.CompletedTask; }
        public Task<AccountSecurityChangeReceipt> RevokeAllSessionsAsync(
            string currentPassword, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class LifecycleStub : IAppLifecycleCoordinator
    {
        public AppLifecycleIntent? Intent { get; private set; }
        public VaultSessionLease? Expected { get; private set; }
        public bool IsTransitioning => false;
        public Task RequestAsync(AppLifecycleIntent intent, VaultSessionLease? expectedSession = null)
        { Intent = intent; Expected = expectedSession; return Task.CompletedTask; }
        public Task<bool> RequestIfAsync(AppLifecycleIntent intent, Func<bool> predicate) => Task.FromResult(predicate());
        public bool MarkSessionReady() => true;
        public bool TryRunSensitive(Func<bool> action) => action();
        public void FailClosedSynchronously() { }
    }
}
