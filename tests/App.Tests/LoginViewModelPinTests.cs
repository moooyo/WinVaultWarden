using App.ViewModels;
using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public class LoginViewModelPinTests
{
    [Fact]
    public void PrepareUnlock_WhenPinIsSet_GoesToPinUnlockStage()
    {
        var auth = new FakeAuthService();
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);

        vm.PrepareUnlock("https://vault.example", "me@example.com");

        Assert.Equal(LoginStage.PinUnlock, vm.Stage);
        Assert.True(vm.IsPinUnlockStage);
        Assert.Equal(LoginStatusKind.Info, vm.StatusKind);
    }

    [Fact]
    public void PrepareUnlock_WhenPinIsNotSet_GoesToUnlockStage()
    {
        var auth = new FakeAuthService();
        var pin = new FakePinService { IsPinSet = false };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);

        vm.PrepareUnlock("https://vault.example", "me@example.com");

        Assert.Equal(LoginStage.Unlock, vm.Stage);
        Assert.False(vm.IsPinUnlockStage);
    }

    [Fact]
    public async Task LoginCommand_PinUnlockStage_CallsUnlockWithPinAndInvokesSuccess()
    {
        var auth = new FakeAuthService
        {
            UnlockWithPinResult = new AuthResult.Success(
                TestSessionLeases.CreateSessionLease()),
        };
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);
        var success = false;
        vm.SetSuccessCallback(_ => success = true);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.Pin = "1234";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("1234", auth.UnlockPin);
        Assert.True(success);
    }

    [Fact]
    public async Task LoginCommand_PinUnlockStage_PinClearedFallsBackToMasterPassword()
    {
        var auth = new FakeAuthService { UnlockWithPinResult = new AuthResult.PinCleared("PIN 已清除") };
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.Pin = "1234";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginStage.Unlock, vm.Stage);
        Assert.Equal("PIN 已清除", vm.Status);
        Assert.Equal(string.Empty, vm.Pin);
    }

    [Fact]
    public async Task LoginCommand_KeyConnectorPinCleared_FallsBackToKeyConnector()
    {
        var auth = new FakeAuthService
        {
            UnlockWithPinResult = new AuthResult.PinCleared("PIN 已清除"),
        };
        var pin = new FakePinService { IsPinSet = true };
        var store = new Vault.MemoryTokenStore();
        var session = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-key-connector",
            ServerEndpoints.FromWebVault("https://vault.example"),
            7,
            "me@example.com",
            "device-id",
            "refresh-token",
            "2.protected-user-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            unlockMethod: PersistedSessionUnlockMethod.KeyConnector,
            keyConnectorUrl: "https://keys.example/base");
        store.Save(session);
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            pin: pin,
            tokenStore: store);
        vm.PrepareUnlock(session.ServerUrl, session.Email);
        vm.Pin = "1234";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginStage.KeyConnectorUnlock, vm.Stage);
        Assert.Equal("PIN 已清除", vm.Status);
        Assert.Equal(string.Empty, vm.Pin);
    }

    [Fact]
    public async Task LoginCommand_PinUnlockStage_FailureClearsPinAndStaysOnStage()
    {
        var auth = new FakeAuthService
        {
            UnlockWithPinResult = new AuthResult.Failure(
                "PIN 错误",
                AuthFailureKind.SafeMessage),
        };
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.Pin = "1234";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginStage.PinUnlock, vm.Stage);
        Assert.Equal("PIN 错误", vm.Status);
        Assert.Equal(string.Empty, vm.Pin);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
        Assert.Equal(LoginFocusTarget.Pin, vm.FocusTarget);
    }

    [Fact]
    public void UseMasterPasswordCommand_SwitchesToUnlockStage()
    {
        var auth = new FakeAuthService();
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.Pin = "1234";

        vm.UseMasterPasswordCommand.Execute(null);

        Assert.Equal(LoginStage.Unlock, vm.Stage);
        Assert.Equal(string.Empty, vm.Pin);
    }

    [Fact]
    public async Task LoginCommand_PinUnlockStage_EmptyPinShowsStatusAndDoesNotCallAuth()
    {
        var auth = new FakeAuthService();
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.Pin = string.Empty;

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Null(auth.UnlockPin);
        Assert.Equal("请输入 PIN。", vm.Status);
    }

    private sealed class FakeAuthService : IAuthService
    {
        private readonly Vault.VaultSession _attempts = new();
        public AuthResult LoginResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult SubmitResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockWithPinResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockHelloResult { get; init; } = new AuthResult.Failure("not set");
        public (string ServerUrl, string Email, string Password)? LoginCall { get; private set; }
        public string? SubmitCode { get; private set; }
        public bool? SubmitRememberDevice { get; private set; }
        public string? UnlockPassword { get; private set; }
        public string? UnlockPin { get; private set; }
        public bool UnlockHelloCalled { get; private set; }
        public bool LoggedOut { get; private set; }

        public Core.Session.AuthenticationAttemptLease BeginAuthenticationAttempt() => _attempts.BeginAuthenticationAttempt();
        public void AbandonAuthenticationAttempt(Core.Session.AuthenticationAttemptLease attempt) => _attempts.AbandonAuthenticationAttempt(attempt);
        public Task<AuthResult> LoginAsync(Core.Session.AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default) => LoginAsync(serverUrl, email, masterPassword, ct);
        public Task<AuthResult> SubmitTwoFactorAsync(Core.Session.AuthenticationAttemptLease attempt, string code, int provider, CancellationToken ct = default, bool rememberDevice = true) => SubmitTwoFactorAsync(code, provider, ct, rememberDevice);
        public Task<bool> ResendTwoFactorEmailAsync(Core.Session.AuthenticationAttemptLease attempt, CancellationToken ct = default) => ResendTwoFactorEmailAsync(ct);
        public Task<AuthResult> UnlockAsync(Core.Session.AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default) => UnlockAsync(masterPassword, ct);
        public Task<AuthResult> UnlockWithPinAsync(Core.Session.AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default) => UnlockWithPinAsync(pin, ct);
        public Task<AuthResult> UnlockWithHelloAsync(Core.Session.AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default) => UnlockWithHelloAsync(signature, ct);

        public Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        {
            LoginCall = (serverUrl, email, masterPassword);
            return Task.FromResult(LoginResult);
        }

        public Task<AuthResult> SubmitTwoFactorAsync(string code, int provider, CancellationToken ct = default, bool rememberDevice = true)
        {
            SubmitCode = code;
            SubmitRememberDevice = rememberDevice;
            return Task.FromResult(SubmitResult);
        }

        public Task<bool> ResendTwoFactorEmailAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
        {
            UnlockPassword = masterPassword;
            return Task.FromResult(UnlockResult);
        }

        public Task<AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default)
        {
            UnlockPin = pin;
            return Task.FromResult(UnlockWithPinResult);
        }

        public Task<AuthResult> UnlockWithHelloAsync(byte[] signature, CancellationToken ct = default)
        {
            UnlockHelloCalled = true;
            return Task.FromResult(UnlockHelloResult);
        }

        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LockAsync(Core.Session.VaultSessionLease expectedSession, CancellationToken ct = default) => Task.CompletedTask;

        public Task LogoutAsync(CancellationToken ct = default)
        {
            LoggedOut = true;
            return Task.CompletedTask;
        }

        public async Task<bool> LogoutAsync(Core.Session.VaultSessionLease expectedSession, CancellationToken ct = default)
        {
            await LogoutAsync(ct);
            return true;
        }
    }

    private sealed class FakePinService : IPinService
    {
        public bool IsPinSet { get; set; }

        public void SetPin(string pin) => IsPinSet = true;

        public void ClearPin() => IsPinSet = false;
    }
}
