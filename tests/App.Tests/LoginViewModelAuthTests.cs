using App.Services;
using App.ViewModels;
using Core.Services;
using Xunit;

namespace App.Tests;

public class LoginViewModelAuthTests
{
    [Fact]
    public async Task LoginCommand_AccountStage_ValidatesAndAdvancesToPasswordStage()
    {
        var auth = new FakeAuthService();
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.Equal("vault.example", vm.ServerSummary);
        Assert.Equal("me@example.com", vm.AccountSummary);
        Assert.Null(auth.LoginCall);
    }

    [Fact]
    public async Task LoginCommand_AccountStage_InvalidServerShowsStatus()
    {
        var auth = new FakeAuthService();
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "not-a-url",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsAccountStage);
        Assert.Equal("请输入有效的服务器地址。", vm.Status);
        Assert.True(vm.HasStatus);
    }

    [Fact]
    public async Task LoginCommand_OnSuccess_InvokesCallback()
    {
        var auth = new FakeAuthService { LoginResult = new AuthResult.Success() };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };
        var success = false;
        vm.SetSuccessCallback(() => success = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(success);
        Assert.Equal(("https://vault.example", "me@example.com", "password"), auth.LoginCall);
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task LoginCommand_TwoFactorThenSubmit_UsesSubmitFlow()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([0]),
            SubmitResult = new AuthResult.Success(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };
        var success = false;
        vm.SetSuccessCallback(() => success = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.False(vm.CanEditServer);

        vm.TwoFactorCode = "123456";
        vm.RememberDevice = false;
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("123456", auth.SubmitCode);
        Assert.True(auth.SubmitRememberDevice.HasValue);
        Assert.False(auth.SubmitRememberDevice.Value);
        Assert.True(success);
        Assert.False(vm.IsTwoFactorStage);
    }

    [Fact]
    public async Task LoginCommand_OnSuccess_ClearsMasterPassword()
    {
        var auth = new FakeAuthService { LoginResult = new AuthResult.Success() };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };

        await vm.LoginCommand.ExecuteAsync(null); // Account → Password 阶段
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null); // 登录成功

        Assert.Equal(string.Empty, vm.MasterPassword);
    }

    [Fact]
    public async Task LoginCommand_UnlockStage_CallsUnlock()
    {
        var auth = new FakeAuthService { UnlockResult = new AuthResult.Success() };
        var vm = new LoginViewModel(auth);
        var success = false;
        vm.SetSuccessCallback(() => success = true);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.MasterPassword = "password";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("password", auth.UnlockPassword);
        Assert.True(success);
        Assert.True(vm.IsUnlockStage);
        Assert.False(vm.CanEditServer);
    }

    [Fact]
    public async Task SwitchAccountCommand_LogsOutAndReturnsToAccountStage()
    {
        var auth = new FakeAuthService();
        var vm = new LoginViewModel(auth);
        vm.PrepareUnlock("https://vault.example", "me@example.com");

        await vm.SwitchAccountCommand.ExecuteAsync(null);

        Assert.True(auth.LoggedOut);
        Assert.True(vm.IsAccountStage);
        Assert.Equal("https://vault.bitwarden.com", vm.ServerUrl);
        Assert.Equal(string.Empty, vm.Email);
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task UseDemoVaultCommand_WhenDemoServiceExists_OpensDemoVault()
    {
        var auth = new FakeAuthService();
        var demo = new FakeDemoVaultSessionService();
        var vm = new LoginViewModel(auth, demo);
        var success = false;
        vm.SetSuccessCallback(() => success = true);
        vm.Status = "previous";

        await vm.UseDemoVaultCommand.ExecuteAsync(null);

        Assert.True(demo.Opened);
        Assert.True(success);
        Assert.Equal(string.Empty, vm.Status);
        Assert.True(vm.CanUseDemoVault);
    }

    [Fact]
    public async Task TwoFactor_EmailProvider_ShowsResendAndResendCommandCallsAuth()
    {
        var auth = new FakeAuthService { LoginResult = new AuthResult.TwoFactorRequired([1]) };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };

        await vm.LoginCommand.ExecuteAsync(null);   // Account → Password
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);   // Password → 2FA (email)

        Assert.True(vm.IsTwoFactorStage);
        Assert.True(vm.ShowResendEmailCode);

        await vm.ResendEmailCodeCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.ResendEmailCalls);
        Assert.Equal("验证码已重新发送，请查收邮箱。", vm.Status);
    }

    [Fact]
    public async Task TwoFactor_AuthenticatorOnly_HidesResend()
    {
        var auth = new FakeAuthService { LoginResult = new AuthResult.TwoFactorRequired([0]) };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.False(vm.ShowResendEmailCode);
    }

    [Fact]
    public async Task TwoFactor_MultipleTypedProviders_SelectionPassedToSubmit()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([0, 1]),
            SubmitResult = new AuthResult.Success(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.Equal(2, vm.TwoFactorProviderOptions.Count);
        Assert.True(vm.ShowTwoFactorProviderPicker);

        vm.SelectedTwoFactorProviderIndex = 1; // 选 Email
        vm.TwoFactorCode = "123456";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.SubmitProvider);
        Assert.Equal("123456", auth.SubmitCode);
    }

    [Fact]
    public async Task TwoFactor_SingleTypedProvider_HidesPicker()
    {
        var auth = new FakeAuthService { LoginResult = new AuthResult.TwoFactorRequired([0]) };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2, ServerUrl = "https://vault.example", Email = "me@example.com", MasterPassword = "password",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.Single(vm.TwoFactorProviderOptions);
        Assert.False(vm.ShowTwoFactorProviderPicker);
    }

    [Fact]
    public async Task TwoFactor_OnlyUnsupportedProviders_ShowsUnsupportedMessageAndStaysOnPassword()
    {
        var auth = new FakeAuthService { LoginResult = new AuthResult.TwoFactorRequired([7]) }; // WebAuthn
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2, ServerUrl = "https://vault.example", Email = "me@example.com", MasterPassword = "password",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.False(vm.IsTwoFactorStage);
        Assert.Contains("暂不支持", vm.Status);
    }

    private sealed class FakeAuthService : IAuthService
    {
        public AuthResult LoginResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult SubmitResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockWithPinResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockHelloResult { get; init; } = new AuthResult.Failure("not set");
        public (string ServerUrl, string Email, string Password)? LoginCall { get; private set; }
        public string? SubmitCode { get; private set; }
        public int? SubmitProvider { get; private set; }
        public bool? SubmitRememberDevice { get; private set; }
        public string? UnlockPassword { get; private set; }
        public string? UnlockPin { get; private set; }
        public bool UnlockHelloCalled { get; private set; }
        public bool LoggedOut { get; private set; }

        public Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        {
            LoginCall = (serverUrl, email, masterPassword);
            return Task.FromResult(LoginResult);
        }

        public Task<AuthResult> SubmitTwoFactorAsync(string code, int provider, CancellationToken ct = default, bool rememberDevice = true)
        {
            SubmitCode = code;
            SubmitProvider = provider;
            SubmitRememberDevice = rememberDevice;
            return Task.FromResult(SubmitResult);
        }

        public int ResendEmailCalls { get; private set; }
        public bool ResendResult { get; init; } = true;

        public Task<bool> ResendTwoFactorEmailAsync(CancellationToken ct = default)
        {
            ResendEmailCalls++;
            return Task.FromResult(ResendResult);
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

        public Task<AuthResult> UnlockWithHelloAsync(byte[] signature, CancellationToken ct = default)
        {
            UnlockHelloCalled = true;
            return Task.FromResult(UnlockHelloResult);
        }

        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task LogoutAsync(CancellationToken ct = default)
        {
            LoggedOut = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDemoVaultSessionService : IDemoVaultSessionService
    {
        public bool Opened { get; private set; }

        public Task OpenDemoVaultAsync(CancellationToken ct = default)
        {
            Opened = true;
            return Task.CompletedTask;
        }
    }
}
