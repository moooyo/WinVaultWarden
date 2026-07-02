using App.ViewModels;
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
        var auth = new FakeAuthService { UnlockWithPinResult = new AuthResult.Success() };
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);
        var success = false;
        vm.SetSuccessCallback(() => success = true);
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
    public async Task LoginCommand_PinUnlockStage_FailureClearsPinAndStaysOnStage()
    {
        var auth = new FakeAuthService { UnlockWithPinResult = new AuthResult.Failure("PIN 错误") };
        var pin = new FakePinService { IsPinSet = true };
        var vm = new LoginViewModel(auth, demoVault: null, pin: pin);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.Pin = "1234";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginStage.PinUnlock, vm.Stage);
        Assert.Equal("PIN 错误", vm.Status);
        Assert.Equal(string.Empty, vm.Pin);
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
        public AuthResult LoginResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult SubmitResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockWithPinResult { get; init; } = new AuthResult.Failure("not set");
        public (string ServerUrl, string Email, string Password)? LoginCall { get; private set; }
        public string? SubmitCode { get; private set; }
        public bool? SubmitRememberDevice { get; private set; }
        public string? UnlockPassword { get; private set; }
        public string? UnlockPin { get; private set; }
        public bool LoggedOut { get; private set; }

        public Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        {
            LoginCall = (serverUrl, email, masterPassword);
            return Task.FromResult(LoginResult);
        }

        public Task<AuthResult> SubmitTwoFactorAsync(string code, CancellationToken ct = default, bool rememberDevice = true)
        {
            SubmitCode = code;
            SubmitRememberDevice = rememberDevice;
            return Task.FromResult(SubmitResult);
        }

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

        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task LogoutAsync(CancellationToken ct = default)
        {
            LoggedOut = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePinService : IPinService
    {
        public bool IsPinSet { get; set; }

        public void SetPin(string pin) => IsPinSet = true;

        public void ClearPin() => IsPinSet = false;
    }
}
