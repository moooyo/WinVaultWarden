using App.Services;
using App.ViewModels;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
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

        Assert.False(vm.ShowAccountContext);

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.True(vm.ShowAccountContext);
        Assert.Equal("https://vault.example", vm.ServerSummary);
        Assert.Equal("me@example.com", vm.AccountSummary);
        Assert.Null(auth.LoginCall);
    }

    [Fact]
    public void ServerSummary_PreservesSchemePortAndBasePath()
    {
        var vm = new LoginViewModel(new FakeAuthService())
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "http://127.0.0.1:18082/passwords/",
        };

        Assert.Equal("http://127.0.0.1:18082/passwords", vm.ServerSummary);
    }

    [Fact]
    public void SelectingSelfHosted_ClearsExampleValueAndRequestsServerFocus()
    {
        var vm = new LoginViewModel(new FakeAuthService());

        vm.SelectedServerOptionIndex = 2;

        Assert.Equal(string.Empty, vm.ServerUrl);
        Assert.Equal(LoginFocusTarget.ServerUrl, vm.FocusTarget);
        Assert.True(vm.FocusRequestId > 0);
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
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
        Assert.Equal(LoginFocusTarget.ServerUrl, vm.FocusTarget);
    }

    [Theory]
    [InlineData("https://user@vault.example")]
    [InlineData("https://vault.example/path?query=1")]
    [InlineData("https://vault.example/path#fragment")]
    public async Task LoginCommand_AccountStage_RejectsUnsafeServerEndpoint(string serverUrl)
    {
        var auth = new FakeAuthService();
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = serverUrl,
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsAccountStage);
        Assert.Null(auth.LoginCall);
        Assert.Equal("请输入有效的服务器地址。", vm.Status);
        Assert.Equal(LoginFocusTarget.ServerUrl, vm.FocusTarget);
    }

    [Fact]
    public async Task LoginCommand_OnSuccess_InvokesCallback()
    {
        var auth = new FakeAuthService { LoginResult = Success() };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };
        var success = false;
        vm.SetSuccessCallback(_ => success = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(success);
        Assert.Equal(("https://vault.example", "me@example.com", "password"), auth.LoginCall);
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task LoginCommand_NewDeviceVerificationThenSubmit_UsesBoundChallengeFlow()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.DeviceVerificationRequired(),
            DeviceVerificationResult = Success(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "temporary-secret",
        };
        var success = false;
        vm.SetSuccessCallback(_ => success = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "temporary-secret";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsDeviceVerificationStage);
        Assert.Equal(string.Empty, vm.MasterPassword);
        Assert.Equal(LoginFocusTarget.DeviceVerificationCode, vm.FocusTarget);
        Assert.False(success);

        vm.DeviceVerificationCode = "123456";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("123456", auth.DeviceVerificationCode);
        Assert.True(success);
        Assert.Equal(string.Empty, vm.DeviceVerificationCode);
    }

    [Fact]
    public async Task LoginCommand_InvalidDeviceVerificationCode_RemainsRetryable()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.DeviceVerificationRequired(),
            DeviceVerificationResult = new AuthResult.Failure(
                "Invalid New Device OTP",
                AuthFailureKind.InvalidCredentials),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);
        vm.DeviceVerificationCode = "000000";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsDeviceVerificationStage);
        Assert.Contains("验证码", vm.Status);
        Assert.Equal(0, auth.AbandonCalls);
        Assert.Equal(LoginFocusTarget.DeviceVerificationCode, vm.FocusTarget);
    }

    [Fact]
    public async Task ResendNewDeviceCode_UsesCurrentAuthenticationAttempt()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.DeviceVerificationRequired(),
            ResendNewDeviceResult = true,
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);
        await vm.ResendNewDeviceCodeCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.ResendNewDeviceCalls);
        Assert.Contains("已发送", vm.Status);
        Assert.True(vm.IsDeviceVerificationStage);
    }

    [Fact]
    public async Task LoginCommand_RequiredPasswordChange_EntersRestrictedStageWithoutVaultCallback()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.PasswordChangeRequired(
                lease,
                RequiredPasswordChangeReason.AdministratorTemporaryPassword,
                new MasterPasswordPolicyRequirements(
                    MinLength: 18,
                    RequireUpper: true,
                    RequireNumbers: true)),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };
        var success = false;
        vm.SetSuccessCallback(_ => success = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "temporary-password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordChangeRequiredStage);
        Assert.False(success);
        Assert.Equal(string.Empty, vm.MasterPassword);
        Assert.Contains("18", vm.RequiredPasswordPolicySummary);
        Assert.Contains("大写", vm.RequiredPasswordPolicySummary);
        Assert.Contains("数字", vm.RequiredPasswordPolicySummary);
        Assert.Equal(LoginFocusTarget.NewMasterPassword, vm.FocusTarget);
    }

    [Fact]
    public async Task LoginCommand_RequiredPasswordChange_SubmitsAndReturnsToPasswordLogin()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.PasswordChangeRequired(
                lease,
                RequiredPasswordChangeReason.WeakMasterPassword,
                new MasterPasswordPolicyRequirements(MinLength: 16)),
            RequiredPasswordChangeResult = new AuthResult.PasswordChanged(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "old-password";
        await vm.LoginCommand.ExecuteAsync(null);
        vm.NewMasterPassword = "New-Master-Password#42";
        vm.ConfirmNewMasterPassword = "New-Master-Password#42";
        vm.MasterPasswordHint = "new hint";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("New-Master-Password#42", auth.RequiredPasswordChangeCall?.NewPassword);
        Assert.Equal("new hint", auth.RequiredPasswordChangeCall?.Hint);
        Assert.True(vm.IsPasswordStage);
        Assert.Contains("已更新", vm.Status);
        Assert.Equal(string.Empty, vm.NewMasterPassword);
        Assert.Equal(string.Empty, vm.ConfirmNewMasterPassword);
        Assert.Equal(string.Empty, vm.MasterPasswordHint);
    }

    [Fact]
    public async Task LoginCommand_RequiredPasswordChange_RejectsMismatchBeforeServiceCall()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.PasswordChangeRequired(
                lease,
                RequiredPasswordChangeReason.WeakMasterPassword,
                null),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "old-password";
        await vm.LoginCommand.ExecuteAsync(null);
        vm.NewMasterPassword = "New-Master-Password#42";
        vm.ConfirmNewMasterPassword = "different-password-42";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Null(auth.RequiredPasswordChangeCall);
        Assert.True(vm.IsPasswordChangeRequiredStage);
        Assert.Contains("不一致", vm.Status);
    }

    [Fact]
    public async Task LoginCommand_UncertainPasswordChange_ReturnsToLoginWithoutRetryingWrite()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.PasswordChangeRequired(
                lease,
                RequiredPasswordChangeReason.AdministratorTemporaryPassword,
                null),
            RequiredPasswordChangeResult = new AuthResult.PasswordChangeUncertain(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "old-password";
        await vm.LoginCommand.ExecuteAsync(null);
        vm.NewMasterPassword = "New-Master-Password#42";
        vm.ConfirmNewMasterPassword = "New-Master-Password#42";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.Contains("可能", vm.Status);
        Assert.NotNull(auth.RequiredPasswordChangeCall);
    }

    [Fact]
    public async Task LoginCommand_TwoFactorThenSubmit_UsesSubmitFlow()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([0]),
            SubmitResult = Success(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            MasterPassword = "password",
        };
        var success = false;
        vm.SetSuccessCallback(_ => success = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.False(vm.CanEditServer);
        Assert.Equal(LoginStatusKind.Info, vm.StatusKind);
        Assert.Equal(LoginFocusTarget.TwoFactorCode, vm.FocusTarget);

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
        var auth = new FakeAuthService { LoginResult = Success() };
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
        var auth = new FakeAuthService { UnlockResult = Success() };
        var vm = new LoginViewModel(auth);
        var success = false;
        vm.SetSuccessCallback(_ => success = true);
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.MasterPassword = "password";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("password", auth.UnlockPassword);
        Assert.True(success);
        Assert.True(vm.IsUnlockStage);
        Assert.False(vm.CanEditServer);
    }

    [Fact]
    public async Task LoginCommand_KeyConnectorUnlockStage_DoesNotRequireMasterPassword()
    {
        var auth = new FakeAuthService
        {
            UnlockWithKeyConnectorResult = Success(),
        };
        var store = new Vault.MemoryTokenStore();
        var persisted = SavedSession("account-key-connector", "https://vault.example") with
        {
            UnlockMethod = PersistedSessionUnlockMethod.KeyConnector,
            KeyConnectorUrl = "https://keys.example/base",
        };
        store.Save(persisted);
        var vm = new LoginViewModel(auth, null, tokenStore: store);
        var success = false;
        vm.SetSuccessCallback(_ => success = true);

        vm.PrepareUnlock(persisted.ServerUrl, persisted.Email);
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginStage.KeyConnectorUnlock, vm.Stage);
        Assert.True(vm.IsKeyConnectorUnlockStage);
        Assert.True(vm.ShowUnlockPanel);
        Assert.True(auth.UnlockWithKeyConnectorCalled);
        Assert.True(success);
        Assert.Equal(string.Empty, vm.MasterPassword);
    }

    [Fact]
    public async Task SwitchAccountCommand_ShowsSavedAccountsWithoutLoggingOut()
    {
        var auth = new FakeAuthService();
        var store = new Vault.MemoryTokenStore();
        var first = SavedSession("account-a", "https://a.example");
        var second = SavedSession("account-b", "https://b.example");
        store.Save(first);
        store.Save(second);
        var vm = new LoginViewModel(
            auth,
            null,
            savedAccountService: new SavedAccountService(store),
            tokenStore: store);
        vm.PrepareUnlock(second.ServerUrl, second.Email);
        vm.Pin = "old-pin";

        await vm.SwitchAccountCommand.ExecuteAsync(null);

        Assert.True(vm.IsAccountStage);
        Assert.True(vm.ShowSavedAccounts);
        Assert.Equal(2, vm.SavedAccounts.Count);
        Assert.False(auth.LoggedOut);
        Assert.Equal(string.Empty, vm.Pin);

        var selected = vm.SavedAccounts.Single(account => account.AccountId == first.AccountId);
        vm.SelectSavedAccountCommand.Execute(selected);

        Assert.True(vm.IsUnlockStage);
        Assert.Equal(first.Email, vm.Email);
        Assert.Equal(first.ServerUrl, vm.ServerUrl);
        Assert.True(store.TryLoad(out var active));
        Assert.Equal(first.AccountId, active.AccountId);
    }

    [Fact]
    public async Task UseDemoVaultCommand_WhenDemoServiceExists_OpensDemoVault()
    {
        var auth = new FakeAuthService();
        var demo = new FakeDemoVaultSessionService();
        var vm = new LoginViewModel(auth, demo);
        var success = false;
        vm.SetSuccessCallback(_ => success = true);
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
        Assert.Equal(LoginStatusKind.Success, vm.StatusKind);
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
            SubmitResult = Success(),
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
    public async Task TwoFactor_OnlyUnsupportedProviders_OffersRecoveryCodeWithoutDroppingAttempt()
    {
        var auth = new FakeAuthService { LoginResult = new AuthResult.TwoFactorRequired([7]) }; // WebAuthn
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2, ServerUrl = "https://vault.example", Email = "me@example.com", MasterPassword = "password",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.True(vm.IsRecoveryCodeMode);
        Assert.True(vm.ShowRecoveryCodeWarning);
        Assert.False(vm.ShowRecoveryCodeSwitch);
        Assert.Equal(8, Assert.Single(vm.TwoFactorProviderOptions).Type);
        Assert.Contains("恢复代码", vm.Status);
        Assert.Equal(0, auth.AbandonCalls);
    }

    [Fact]
    public async Task TwoFactor_LegacyDuoOnly_ExplainsServerRemediationAndOffersRecoveryCode()
    {
        const string legacyDuo =
            "{\"Host\":\"api-test.duosecurity.com\","
            + "\"Signature\":\"TX|bWVleGFtcGxl|0000000000000000000000000000000000000000:"
            + "APP|bWVleGFtcGxl|1111111111111111111111111111111111111111\"}";
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired(
                [(int)TwoFactorProviderType.Duo],
                new Dictionary<int, string>
                {
                    [(int)TwoFactorProviderType.Duo] = legacyDuo,
                }),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.True(vm.IsRecoveryCodeMode);
        Assert.Contains("DUO_USE_IFRAME", vm.Status);
        Assert.Contains("恢复码", vm.Status);
        Assert.Equal(0, auth.AbandonCalls);
    }

    [Fact]
    public async Task TwoFactor_LegacyDuoWithSupportedFactor_DoesNotReplaceNormalPrompt()
    {
        const string legacyDuo =
            "{\"Host\":\"api-test.duosecurity.com\","
            + "\"Signature\":\"TX|bWVleGFtcGxl|0000000000000000000000000000000000000000:"
            + "APP|bWVleGFtcGxl|1111111111111111111111111111111111111111\"}";
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired(
                [(int)TwoFactorProviderType.Duo, (int)TwoFactorProviderType.Authenticator],
                new Dictionary<int, string>
                {
                    [(int)TwoFactorProviderType.Duo] = legacyDuo,
                }),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecoveryCodeMode);
        Assert.DoesNotContain("DUO_USE_IFRAME", vm.Status);
        Assert.Equal((int)TwoFactorProviderType.Authenticator, Assert.Single(vm.TwoFactorProviderOptions).Type);
    }

    [Fact]
    public async Task TwoFactor_YubiKeyOtp_IsTypedAndSubmittedWithoutTruncation()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([3]),
            SubmitResult = Success(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.Equal("YubiKey OTP", Assert.Single(vm.TwoFactorProviderOptions).DisplayName);
        Assert.Equal("YubiKey OTP", vm.TwoFactorInputHeader);
        var otp = new string('c', 44);
        vm.TwoFactorCode = otp;
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(3, auth.SubmitProvider);
        Assert.Equal(otp, auth.SubmitCode);
    }

    [Fact]
    public async Task TwoFactor_RecoveryCode_SubmitsSpecialProviderWithoutRememberingDevice()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([0, 1]),
            SubmitResult = Success(),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
            RememberDevice = true,
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "password";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.True(vm.ShowRecoveryCodeSwitch);

        vm.ToggleRecoveryCodeModeCommand.Execute(null);

        Assert.True(vm.IsRecoveryCodeMode);
        Assert.True(vm.ShowRecoveryCodeWarning);
        Assert.False(vm.ShowRememberDevice);
        Assert.Equal("恢复代码", vm.TwoFactorInputHeader);
        Assert.Equal("恢复并登录", vm.PrimaryButtonText);
        vm.TwoFactorCode = "recovery-code-value";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(8, auth.SubmitProvider);
        Assert.Equal("recovery-code-value", auth.SubmitCode);
        Assert.False(auth.SubmitRememberDevice);
    }

    [Fact]
    public async Task UnlockWithHello_WhenGestureSucceeds_CallsAuthUnlockWithHello()
    {
        var auth = new FakeAuthService { UnlockHelloResult = Success() };
        var hello = new FakeHello { GetResult = Enumerable.Repeat((byte)7, 256).ToArray() };
        var unlock = new FakeHelloUnlock { Set = true };
        var vm = new LoginViewModel(auth, null, hello: hello, helloUnlock: unlock);
        vm.PrepareUnlock("https://vault.example", "me@example.com");

        Assert.True(vm.ShowHelloUnlock);
        await vm.UnlockWithHelloCommand.ExecuteAsync(null);

        Assert.True(auth.UnlockHelloCalled);
    }

    [Fact]
    public async Task UnlockWithHello_WhenGestureCancelled_DoesNotCallAuth()
    {
        var auth = new FakeAuthService();
        var hello = new FakeHello { GetResult = null };
        var unlock = new FakeHelloUnlock { Set = true };
        var vm = new LoginViewModel(auth, null, hello: hello, helloUnlock: unlock);
        vm.PrepareUnlock("https://vault.example", "me@example.com");

        await vm.UnlockWithHelloCommand.ExecuteAsync(null);

        Assert.False(auth.UnlockHelloCalled);
    }

    // H7 review fix: UnlockWithHelloAsync must participate in the IsBusy guard so it can't
    // run concurrently with LoginAsync's master-password/PIN unlock (shared session/token state).
    [Fact]
    public async Task UnlockWithHello_GuardsWithIsBusy()
    {
        var auth = new FakeAuthService { UnlockHelloResult = Success() };
        var hello = new FakeHello { GetResult = Enumerable.Repeat((byte)7, 256).ToArray() };
        var unlock = new FakeHelloUnlock { Set = true };
        var vm = new LoginViewModel(auth, null, hello: hello, helloUnlock: unlock);
        vm.PrepareUnlock("https://vault.example", "me@example.com");

        await vm.UnlockWithHelloCommand.ExecuteAsync(null);

        Assert.True(auth.UnlockHelloCalled);
        Assert.False(vm.IsBusy); // cleared in `finally` after completion

        // While a Hello (or master-password/PIN) unlock is already in flight, re-entering must
        // be a no-op: no second call into Hello/auth, and IsBusy stays as the caller left it.
        var auth2 = new FakeAuthService { UnlockHelloResult = Success() };
        var vm2 = new LoginViewModel(auth2, null, hello: hello, helloUnlock: unlock);
        vm2.PrepareUnlock("https://vault.example", "me@example.com");
        vm2.IsBusy = true;

        await vm2.UnlockWithHelloCommand.ExecuteAsync(null);

        Assert.False(auth2.UnlockHelloCalled);
        Assert.True(vm2.IsBusy);
    }

    [Fact]
    public async Task ExpectedNetworkException_ShowsStableStatus()
    {
        var vm = new LoginViewModel(new ThrowingAuth(
            new HttpRequestException("internal endpoint detail")));
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.MasterPassword = "master";

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("无法连接服务器，请检查网络后重试。", vm.Status);
        Assert.DoesNotContain("internal endpoint detail", vm.Status);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
        Assert.Equal(LoginFocusTarget.MasterPassword, vm.FocusTarget);
    }

    [Fact]
    public async Task UnspecifiedFailure_DoesNotExposeTechnicalMessage()
    {
        const string technicalDetail =
            "No connection could be made because the target machine actively refused it. (127.0.0.1:18082)";
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.Failure(technicalDetail),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "http://127.0.0.1:18082",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal("登录失败，请稍后重试。", vm.Status);
        Assert.DoesNotContain("127.0.0.1", vm.Status);
        Assert.Equal(LoginFocusTarget.MasterPassword, vm.FocusTarget);
    }

    [Theory]
    [InlineData(AuthFailureKind.Network, "无法连接服务器，请检查网络后重试。")]
    [InlineData(AuthFailureKind.RateLimited, "登录请求过于频繁，请稍后重试。")]
    public async Task TypedAuthenticationFailure_ShowsStableLocalizedMessage(
        AuthFailureKind kind,
        string expectedStatus)
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.Failure(
                "sensitive endpoint detail",
                kind),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(expectedStatus, vm.Status);
        Assert.DoesNotContain("sensitive endpoint detail", vm.Status);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
    }

    [Theory]
    [InlineData(AuthFailureKind.Network, "无法连接服务器，请检查网络后重试。")]
    [InlineData(AuthFailureKind.RateLimited, "登录请求过于频繁，请稍后重试。")]
    public async Task TwoFactorTransientFailure_PreservesAttemptAndCanRetry(
        AuthFailureKind kind,
        string expectedStatus)
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([0]),
            SubmitResult = new AuthResult.Failure(
                "sensitive endpoint detail",
                kind),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };
        var succeeded = false;
        vm.SetSuccessCallback(_ => succeeded = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);
        vm.TwoFactorCode = "123456";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.Equal(expectedStatus, vm.Status);
        Assert.Equal(LoginFocusTarget.TwoFactorCode, vm.FocusTarget);
        Assert.Equal(0, auth.AbandonCalls);

        auth.SubmitResult = Success();
        vm.TwoFactorCode = "654321";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(succeeded);
        Assert.Equal("654321", auth.SubmitCode);
    }

    [Fact]
    public async Task TwoFactorExpiredFailure_ReturnsToPasswordStage()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([0]),
            SubmitResult = new AuthResult.Failure(
                "expired internal detail",
                AuthFailureKind.SessionExpired),
        };
        var vm = new LoginViewModel(auth)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);
        vm.TwoFactorCode = "123456";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsPasswordStage);
        Assert.Equal(string.Empty, vm.TwoFactorCode);
        Assert.Equal("认证会话已失效，请重试。", vm.Status);
        Assert.Equal(LoginFocusTarget.MasterPassword, vm.FocusTarget);
        Assert.True(auth.AbandonCalls > 0);
    }

    [Fact]
    public async Task UnknownException_IsNotSwallowed()
    {
        var vm = new LoginViewModel(new ThrowingAuth(
            new InvalidOperationException("programmer fault")));
        vm.PrepareUnlock("https://vault.example", "me@example.com");
        vm.MasterPassword = "master";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.LoginCommand.ExecuteAsync(null));

        Assert.Equal("programmer fault", exception.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SsoLogin_StandardAccountMovesToMasterPasswordUnlock()
    {
        var auth = new FakeAuthService
        {
            SsoResult = new AuthResult.SsoAuthenticated(TestSessionLeases.CreateSessionLease()),
        };
        var authorization = new FakeSsoAuthorizationService();
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: authorization)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginWithSsoCommand.ExecuteAsync(null);

        Assert.True(vm.IsUnlockStage);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsSsoPending);
        Assert.Contains("SSO 登录成功", vm.Status);
        Assert.Equal("https://vault.example", authorization.Endpoints!.WebVault.Value);
        Assert.Equal("me@example.com", authorization.Email);
        Assert.Equal("http://localhost:8065", auth.SsoAuthorization!.RedirectUri);
    }

    [Fact]
    public async Task SsoTwoFactor_BackReturnsToAccountInsteadOfMasterPassword()
    {
        var auth = new FakeAuthService
        {
            SsoResult = new AuthResult.TwoFactorRequired([0]),
        };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: new FakeSsoAuthorizationService())
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginWithSsoCommand.ExecuteAsync(null);
        Assert.True(vm.IsTwoFactorStage);

        vm.BackCommand.Execute(null);

        Assert.True(vm.IsAccountStage);
        Assert.False(vm.IsPasswordStage);
        Assert.Equal("me@example.com", vm.Email);
    }

    [Fact]
    public async Task SsoTokenExchange_RemainsCancellableAfterBrowserCallback()
    {
        var auth = new FakeAuthService { BlockSsoUntilCancelled = true };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: new FakeSsoAuthorizationService())
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        var operation = vm.LoginWithSsoCommand.ExecuteAsync(null);
        for (var attempt = 0; attempt < 100 && auth.SsoAuthorization is null; attempt++)
            await Task.Delay(1);

        Assert.NotNull(auth.SsoAuthorization);
        Assert.True(vm.IsSsoPending);
        Assert.True(vm.ShowCancelSso);
        vm.CancelSsoCommand.Execute(null);
        await operation;

        Assert.False(vm.IsSsoPending);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task TrustedDeviceSession_PrimaryActionRequiresFreshSso()
    {
        var auth = new FakeAuthService { SsoResult = Success() };
        var authorization = new FakeSsoAuthorizationService();
        var store = new Vault.MemoryTokenStore();
        var persisted = SavedSession("trusted-account", "https://vault.example") with
        {
            DeviceIdentifier = "11111111-2222-3333-4444-555555555555",
            UnlockMethod = PersistedSessionUnlockMethod.TrustedDeviceSso,
        };
        store.Save(persisted);
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: authorization,
            tokenStore: store);
        var succeeded = false;
        vm.SetSuccessCallback(_ => succeeded = true);

        vm.PrepareUnlock(persisted.ServerUrl, persisted.Email);
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginStage.TrustedDeviceSso, vm.Stage);
        Assert.True(vm.IsTrustedDeviceSsoStage);
        Assert.True(succeeded);
        Assert.NotNull(auth.SsoAuthorization);
        Assert.Equal(persisted.Email, authorization.Email);
        Assert.Equal(string.Empty, vm.MasterPassword);
    }

    [Fact]
    public async Task TrustedDeviceApproval_UsesFingerprintAndCompletesSession()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            SsoResult = new AuthResult.TrustedDeviceApprovalRequired(
                lease,
                HasAdminApproval: false),
            TrustedDeviceApprovalResult = new AuthResult.Success(lease),
        };
        var approval = new FakeTrustedDeviceApprovalService();
        var authorization = new FakeSsoAuthorizationService();
        var store = new Vault.MemoryTokenStore();
        var persisted = SavedSession("test-account", "https://vault.example") with
        {
            DeviceIdentifier = "11111111-2222-3333-4444-555555555555",
            UnlockMethod = PersistedSessionUnlockMethod.TrustedDeviceSso,
        };
        store.Save(persisted);
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: authorization,
            tokenStore: store,
            trustedDeviceApproval: approval)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = persisted.ServerUrl,
            Email = persisted.Email,
        };
        var succeeded = false;
        vm.SetSuccessCallback(_ => succeeded = true);

        await vm.LoginWithSsoCommand.ExecuteAsync(null);
        Assert.True(vm.IsTrustedDeviceApprovalStage);
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(succeeded);
        Assert.Equal("alpha-bravo-charlie-delta-echo", approval.Fingerprint);
        Assert.True(auth.TrustCurrentDeviceAfterApproval);
        Assert.Equal(64, auth.ApprovedUserKeyLength);
        Assert.Equal(TrustedDeviceApprovalMethod.Device, approval.Method);
        Assert.Equal(string.Empty, vm.ApprovalFingerprint);
    }

    [Fact]
    public async Task TrustedDeviceApproval_AdminOnlySelectsAdministrator()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            SsoResult = new AuthResult.TrustedDeviceApprovalRequired(
                lease,
                HasAdminApproval: true,
                HasLoginApprovingDevice: false),
            TrustedDeviceApprovalResult = new AuthResult.Success(lease),
        };
        var approval = new FakeTrustedDeviceApprovalService();
        var vm = CreateTrustedDeviceApprovalViewModel(auth, approval, lease);

        await vm.LoginWithSsoCommand.ExecuteAsync(null);
        Assert.False(vm.ShowApprovalMethodPicker);
        Assert.Single(vm.ApprovalMethodOptions);
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(TrustedDeviceApprovalMethod.Administrator, approval.Method);
    }

    [Fact]
    public async Task TrustedDeviceApproval_BothMethodsLetsUserChooseAdministrator()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            SsoResult = new AuthResult.TrustedDeviceApprovalRequired(
                lease,
                HasAdminApproval: true,
                HasLoginApprovingDevice: true),
            TrustedDeviceApprovalResult = new AuthResult.Success(lease),
        };
        var approval = new FakeTrustedDeviceApprovalService();
        var vm = CreateTrustedDeviceApprovalViewModel(auth, approval, lease);

        await vm.LoginWithSsoCommand.ExecuteAsync(null);
        Assert.True(vm.ShowApprovalMethodPicker);
        Assert.Equal(2, vm.ApprovalMethodOptions.Count);
        vm.SelectedApprovalMethodIndex = 1;
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(TrustedDeviceApprovalMethod.Administrator, approval.Method);
    }

    [Fact]
    public async Task TdeV2Jit_RequiresConfirmationThenCompletesProvisioning()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            SsoResult = new AuthResult.TdeV2JitProvisioningRequired(
                lease,
                "enterprise-sso"),
            TdeV2JitResult = new AuthResult.Success(lease),
        };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: new FakeSsoAuthorizationService())
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };
        var succeeded = false;
        vm.SetSuccessCallback(_ => succeeded = true);

        await vm.LoginWithSsoCommand.ExecuteAsync(null);

        Assert.True(vm.IsTdeV2JitProvisioningStage);
        Assert.Equal("完成账户初始化", vm.PrimaryButtonText);
        Assert.True(vm.TrustCurrentDeviceAfterApproval);

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(succeeded);
        Assert.Equal("enterprise-sso", auth.TdeV2JitOrganizationIdentifier);
        Assert.True(auth.TrustCurrentDeviceAfterJit);
    }

    [Fact]
    public async Task TdeV2Jit_ResumeLocksPersistedTrustChoice()
    {
        var lease = TestSessionLeases.CreateSessionLease();
        var auth = new FakeAuthService
        {
            SsoResult = new AuthResult.TdeV2JitProvisioningRequired(
                lease,
                "enterprise-sso",
                RequiredTrustCurrentDevice: false),
        };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: new FakeSsoAuthorizationService())
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginWithSsoCommand.ExecuteAsync(null);

        Assert.True(vm.IsTdeV2JitProvisioningStage);
        Assert.False(vm.TrustCurrentDeviceAfterApproval);
        Assert.False(vm.CanChooseTrustCurrentDeviceAfterJit);
        Assert.Contains("继续原有流程", vm.Status);
    }

    [Fact]
    public async Task WebAuthnTwoFactor_UsesNativeTokenWithoutTextInput()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired(
                [(int)TwoFactorProviderType.WebAuthn],
                new Dictionary<int, string>
                {
                    [(int)TwoFactorProviderType.WebAuthn] = "{\"challenge\":\"provider-data\"}",
                }),
            SubmitResult = Success(),
        };
        var webAuthn = new FakeWebAuthnTwoFactorService { Token = "native-assertion" };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            webAuthnTwoFactor: webAuthn)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };
        var succeeded = false;
        vm.SetSuccessCallback(_ => succeeded = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsWebAuthnMode);
        Assert.False(vm.ShowTwoFactorCodeInput);
        Assert.Equal("使用安全密钥", vm.PrimaryButtonText);

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(succeeded);
        Assert.Equal("native-assertion", auth.SubmitCode);
        Assert.Equal((int)TwoFactorProviderType.WebAuthn, auth.SubmitProvider);
        Assert.Equal("{\"challenge\":\"provider-data\"}", webAuthn.ProviderDataJson);
        Assert.Equal("https://vault.example", webAuthn.Endpoints!.WebVault.Value);
    }

    [Fact]
    public async Task WebAuthnUserCancellation_PreservesTwoFactorAttemptForRetry()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired(
                [(int)TwoFactorProviderType.WebAuthn],
                new Dictionary<int, string>
                {
                    [(int)TwoFactorProviderType.WebAuthn] = "{}",
                }),
        };
        var webAuthn = new FakeWebAuthnTwoFactorService
        {
            Exception = new WebAuthnTwoFactorCancelledException(),
        };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            webAuthnTwoFactor: webAuthn)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsTwoFactorStage);
        Assert.True(vm.IsWebAuthnMode);
        Assert.Contains("已取消", vm.Status);
        Assert.Null(auth.SubmitCode);
        Assert.Equal(0, auth.AbandonCalls);
    }

    [Fact]
    public async Task DuoTwoFactor_UsesCapturedCodeAndStateWithoutTextInput()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired(
                [(int)TwoFactorProviderType.Duo],
                new Dictionary<int, string>
                {
                    [(int)TwoFactorProviderType.Duo] = "{\"AuthUrl\":\"duo\"}",
                }),
            SubmitResult = Success(),
        };
        var duo = new FakeDuoTwoFactorService { Token = "duo-code|duo-state" };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            duoTwoFactor: duo)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };
        var succeeded = false;
        vm.SetSuccessCallback(_ => succeeded = true);

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsDuoMode);
        Assert.False(vm.ShowTwoFactorCodeInput);
        Assert.Equal("打开 Duo", vm.PrimaryButtonText);

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(succeeded);
        Assert.Equal("duo-code|duo-state", auth.SubmitCode);
        Assert.Equal((int)TwoFactorProviderType.Duo, auth.SubmitProvider);
        Assert.Equal("{\"AuthUrl\":\"duo\"}", duo.ProviderDataJson);
    }

    [Fact]
    public async Task DuoUserCancellation_PreservesTwoFactorAttemptForRetry()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired(
                [(int)TwoFactorProviderType.OrganizationDuo],
                new Dictionary<int, string>
                {
                    [(int)TwoFactorProviderType.OrganizationDuo] = "{}",
                }),
        };
        var duo = new FakeDuoTwoFactorService
        {
            Exception = new DuoTwoFactorCancelledException(),
        };
        var vm = new LoginViewModel(
            auth,
            demoVault: null,
            duoTwoFactor: duo)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = "https://vault.example",
            Email = "me@example.com",
        };

        await vm.LoginCommand.ExecuteAsync(null);
        vm.MasterPassword = "master";
        await vm.LoginCommand.ExecuteAsync(null);
        await vm.LoginCommand.ExecuteAsync(null);

        Assert.True(vm.IsDuoMode);
        Assert.Contains("已取消", vm.Status);
        Assert.Null(auth.SubmitCode);
        Assert.Equal(0, auth.AbandonCalls);
    }

    private static LoginViewModel CreateTrustedDeviceApprovalViewModel(
        IAuthService auth,
        ITrustedDeviceApprovalService approval,
        Core.Session.VaultSessionLease lease)
    {
        var store = new Vault.MemoryTokenStore();
        var persisted = SavedSession(lease.AccountId, lease.Endpoints.WebVault.Value) with
        {
            DeviceIdentifier = "11111111-2222-3333-4444-555555555555",
            UnlockMethod = PersistedSessionUnlockMethod.TrustedDeviceSso,
        };
        store.Save(persisted);
        return new LoginViewModel(
            auth,
            demoVault: null,
            ssoAuthorization: new FakeSsoAuthorizationService(),
            tokenStore: store,
            trustedDeviceApproval: approval)
        {
            SelectedServerOptionIndex = 2,
            ServerUrl = persisted.ServerUrl,
            Email = persisted.Email,
        };
    }

    private sealed class ThrowingAuth(Exception exception) : IAuthService
    {
        private readonly Vault.VaultSession _attempts = new();
        private Task<AuthResult> Throw() =>
            Task.FromException<AuthResult>(exception);
        public Core.Session.AuthenticationAttemptLease BeginAuthenticationAttempt() => _attempts.BeginAuthenticationAttempt();
        public void AbandonAuthenticationAttempt(Core.Session.AuthenticationAttemptLease attempt) => _attempts.AbandonAuthenticationAttempt(attempt);
        public Task<AuthResult> LoginAsync(Core.Session.AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default) => Throw();
        public Task<AuthResult> SubmitTwoFactorAsync(Core.Session.AuthenticationAttemptLease attempt, string code, int provider, CancellationToken ct = default, bool rememberDevice = true) => Throw();
        public Task<bool> ResendTwoFactorEmailAsync(Core.Session.AuthenticationAttemptLease attempt, CancellationToken ct = default) => Task.FromException<bool>(exception);
        public Task<AuthResult> UnlockAsync(Core.Session.AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default) => Throw();
        public Task<AuthResult> UnlockWithPinAsync(Core.Session.AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default) => Throw();
        public Task<AuthResult> UnlockWithHelloAsync(Core.Session.AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default) => Throw();
        public Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default) => Throw();
        public Task<AuthResult> SubmitTwoFactorAsync(string code, int provider, CancellationToken ct = default, bool rememberDevice = true) => Throw();
        public Task<bool> ResendTwoFactorEmailAsync(CancellationToken ct = default) => Task.FromException<bool>(exception);
        public Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default) => Throw();
        public Task<AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default) => Throw();
        public Task<AuthResult> UnlockWithHelloAsync(byte[] signature, CancellationToken ct = default) => Throw();
        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LockAsync(Core.Session.VaultSessionLease expectedSession, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> LogoutAsync(Core.Session.VaultSessionLease expectedSession, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static AuthResult.Success Success() =>
        new(TestSessionLeases.CreateSessionLease());

    private sealed class FakeSsoAuthorizationService : ISsoAuthorizationService
    {
        public ServerEndpoints? Endpoints { get; private set; }
        public string? Email { get; private set; }

        public Task<SsoAuthorizationCode> AuthorizeAsync(
            ServerEndpoints endpoints,
            string email,
            CancellationToken ct = default)
        {
            Endpoints = endpoints;
            Email = email;
            return Task.FromResult(new SsoAuthorizationCode(
                "code",
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_",
                "http://localhost:8065",
                "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_",
                "organization"));
        }
    }

    private sealed class FakeTrustedDeviceApprovalService : ITrustedDeviceApprovalService
    {
        public string Fingerprint { get; } = "alpha-bravo-charlie-delta-echo";
        public TrustedDeviceApprovalMethod? Method { get; private set; }

        public Task<TrustedDeviceApprovalRequest> BeginAsync(
            Core.Session.VaultSessionLease lease,
            string email,
            string deviceIdentifier,
            CancellationToken ct = default) =>
            Task.FromResult(new TrustedDeviceApprovalRequest(
                "request-id",
                Fingerprint,
                DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task<TrustedDeviceApprovalRequest> BeginAsync(
            Core.Session.VaultSessionLease lease,
            string email,
            string deviceIdentifier,
            TrustedDeviceApprovalMethod method,
            CancellationToken ct = default)
        {
            Method = method;
            return BeginAsync(lease, email, deviceIdentifier, ct);
        }

        public Task<TrustedDeviceApprovalPollResult> PollAsync(
            Core.Session.VaultSessionLease lease,
            string requestId,
            CancellationToken ct = default) =>
            Task.FromResult(new TrustedDeviceApprovalPollResult(
                TrustedDeviceApprovalStatus.Approved,
                Enumerable.Range(1, 64).Select(value => (byte)value).ToArray()));

        public void Cancel(Core.Session.VaultSessionLease lease)
        {
        }
    }

    private sealed class FakeWebAuthnTwoFactorService : IWebAuthnTwoFactorService
    {
        public bool IsSupported { get; set; } = true;
        public string Token { get; set; } = "token";
        public Exception? Exception { get; set; }
        public string? ProviderDataJson { get; private set; }
        public ServerEndpoints? Endpoints { get; private set; }

        public Task<string> AuthenticateAsync(
            string providerDataJson,
            ServerEndpoints endpoints,
            CancellationToken ct = default)
        {
            ProviderDataJson = providerDataJson;
            Endpoints = endpoints;
            return Exception is null
                ? Task.FromResult(Token)
                : Task.FromException<string>(Exception);
        }
    }

    private sealed class FakeDuoTwoFactorService : IDuoTwoFactorService
    {
        public bool IsSupported { get; set; } = true;
        public bool CanAuthenticateResult { get; set; } = true;
        public string Token { get; set; } = "token";
        public Exception? Exception { get; set; }
        public string? ProviderDataJson { get; private set; }

        public bool CanAuthenticate(string providerDataJson, ServerEndpoints endpoints) =>
            CanAuthenticateResult;

        public Task<string> AuthenticateAsync(
            string providerDataJson,
            ServerEndpoints endpoints,
            CancellationToken ct = default)
        {
            ProviderDataJson = providerDataJson;
            return Exception is null
                ? Task.FromResult(Token)
                : Task.FromException<string>(Exception);
        }
    }

    private sealed class FakeHello : IWindowsHelloAuthenticator
    {
        public bool Available { get; set; } = true;
        public byte[]? CreateResult { get; set; }
        public byte[]? GetResult { get; set; }
        public Task<bool> IsAvailableAsync() => Task.FromResult(Available);
        public Task<byte[]?> CreateProtectorAsync(byte[] challenge, CancellationToken ct = default) => Task.FromResult(CreateResult);
        public Task<byte[]?> GetProtectorAsync(byte[] challenge, CancellationToken ct = default) => Task.FromResult(GetResult);
    }

    private sealed class FakeHelloUnlock : IHelloUnlockService
    {
        public bool Set;
        public bool IsHelloSet => Set;
        public void SetHello(byte[] signature, byte[] challenge) => Set = true;
        public void ClearHello() => Set = false;
        public byte[]? LoadChallenge() => Set ? new byte[32] : null;
    }

    [Fact]
    public async Task PasswordHint_SubmitsOnlyAccountIdentityAndDoesNotClaimEmailDelivery()
    {
        var auth = new FakeAuthService();
        var vm = new LoginViewModel(auth) { Email = "me@example.com" };
        await vm.LoginCommand.ExecuteAsync(null);

        await vm.RequestPasswordHintCommand.ExecuteAsync(null);

        Assert.Equal("me@example.com", auth.PasswordHintCall?.Email);
        Assert.Null(auth.LoginCall);
        Assert.Contains("请求已提交", vm.Status);
        Assert.DoesNotContain("邮件已发送", vm.Status);
        Assert.Equal(LoginStatusKind.Info, vm.StatusKind);
        Assert.True(vm.IsPasswordStage);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("This server is not configured to provide password hints.", "此服务器未启用主密码提示。", LoginStatusKind.Error)]
    [InlineData("Your password hint is: first pet", "主密码提示：first pet", LoginStatusKind.Info)]
    public async Task PasswordHint_PreservesActualServerOutcome(string serverMessage, string expected, LoginStatusKind kind)
    {
        var auth = new FakeAuthService
        {
            PasswordHintOperation = _ => Task.FromException(new Api.VaultWriteException(serverMessage)),
        };
        var vm = new LoginViewModel(auth) { Email = "me@example.com" };
        await vm.LoginCommand.ExecuteAsync(null);

        await vm.RequestPasswordHintCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.Status);
        Assert.Equal(kind, vm.StatusKind);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task PasswordHint_PrepareAnotherAccountRejectsLateResultAndDuplicateRequest()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = new FakeAuthService { PasswordHintOperation = _ => completion.Task };
        var vm = new LoginViewModel(auth) { Email = "me@example.com" };
        await vm.LoginCommand.ExecuteAsync(null);
        var request = vm.RequestPasswordHintCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        await vm.RequestPasswordHintCommand.ExecuteAsync(null);
        Assert.Equal(1, auth.PasswordHintCallCount);

        vm.PrepareAccount("https://another.example", "other@example.com");
        completion.SetResult();
        await request;

        Assert.True(vm.IsAccountStage);
        Assert.Equal("other@example.com", vm.Email);
        Assert.Empty(vm.Status);
        Assert.False(vm.IsBusy);
    }

    private sealed class FakeAuthService : IAuthService
    {
        private readonly Vault.VaultSession _attempts = new();
        public Func<CancellationToken, Task>? PasswordHintOperation { get; init; }
        public (string ServerUrl, string Email)? PasswordHintCall { get; private set; }
        public int PasswordHintCallCount { get; private set; }
        public Task RequestPasswordHintAsync(string serverUrl, string email, CancellationToken ct = default)
        {
            PasswordHintCallCount++;
            PasswordHintCall = (serverUrl, email);
            return PasswordHintOperation?.Invoke(ct) ?? Task.CompletedTask;
        }

        public AuthResult LoginResult { get; set; } = new AuthResult.Failure("not set");
        public AuthResult SsoResult { get; set; } = new AuthResult.Failure("not set");
        public AuthResult SubmitResult { get; set; } = new AuthResult.Failure("not set");
        public AuthResult DeviceVerificationResult { get; set; } = new AuthResult.Failure("not set");
        public AuthResult RequiredPasswordChangeResult { get; set; } = new AuthResult.Failure("not set");
        public AuthResult UnlockResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockWithPinResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockWithKeyConnectorResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult TrustedDeviceApprovalResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult TdeV2JitResult { get; init; } = new AuthResult.Failure("not set");
        public AuthResult UnlockHelloResult { get; init; } = new AuthResult.Failure("not set");
        public (string ServerUrl, string Email, string Password)? LoginCall { get; private set; }
        public SsoAuthorizationCode? SsoAuthorization { get; private set; }
        public string? SubmitCode { get; private set; }
        public string? DeviceVerificationCode { get; private set; }
        public (Core.Session.VaultSessionLease Lease, string NewPassword, string? Hint)? RequiredPasswordChangeCall { get; private set; }
        public int? SubmitProvider { get; private set; }
        public bool? SubmitRememberDevice { get; private set; }
        public string? UnlockPassword { get; private set; }
        public string? UnlockPin { get; private set; }
        public bool UnlockHelloCalled { get; private set; }
        public bool UnlockWithKeyConnectorCalled { get; private set; }
        public bool TrustCurrentDeviceAfterApproval { get; private set; }
        public int ApprovedUserKeyLength { get; private set; }
        public string? TdeV2JitOrganizationIdentifier { get; private set; }
        public bool TrustCurrentDeviceAfterJit { get; private set; }
        public bool LoggedOut { get; private set; }
        public int AbandonCalls { get; private set; }
        public bool BlockSsoUntilCancelled { get; init; }
        public int ResendNewDeviceCalls { get; private set; }
        public bool ResendNewDeviceResult { get; init; } = true;

        public Core.Session.AuthenticationAttemptLease BeginAuthenticationAttempt() => _attempts.BeginAuthenticationAttempt();
        public void AbandonAuthenticationAttempt(Core.Session.AuthenticationAttemptLease attempt)
        {
            AbandonCalls++;
            _attempts.AbandonAuthenticationAttempt(attempt);
        }
        public Task<AuthResult> LoginAsync(Core.Session.AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default) => LoginAsync(serverUrl, email, masterPassword, ct);
        public Task<AuthResult> LoginWithSsoAsync(Core.Session.AuthenticationAttemptLease attempt, string serverUrl, string email, SsoAuthorizationCode authorization, CancellationToken ct = default)
        {
            SsoAuthorization = authorization;
            return BlockSsoUntilCancelled
                ? WaitForCancellationAsync(ct)
                : Task.FromResult(SsoResult);
        }

        private static async Task<AuthResult> WaitForCancellationAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The cancellation wait completed unexpectedly.");
        }
        public Task<AuthResult> SubmitTwoFactorAsync(Core.Session.AuthenticationAttemptLease attempt, string code, int provider, CancellationToken ct = default, bool rememberDevice = true) => SubmitTwoFactorAsync(code, provider, ct, rememberDevice);
        public Task<bool> ResendTwoFactorEmailAsync(Core.Session.AuthenticationAttemptLease attempt, CancellationToken ct = default) => ResendTwoFactorEmailAsync(ct);
        public Task<AuthResult> SubmitNewDeviceVerificationAsync(
            Core.Session.AuthenticationAttemptLease attempt,
            string otp,
            CancellationToken ct = default)
        {
            DeviceVerificationCode = otp;
            return Task.FromResult(DeviceVerificationResult);
        }
        public Task<bool> ResendNewDeviceVerificationAsync(
            Core.Session.AuthenticationAttemptLease attempt,
            CancellationToken ct = default)
        {
            ResendNewDeviceCalls++;
            return Task.FromResult(ResendNewDeviceResult);
        }
        public Task<AuthResult> CompleteRequiredPasswordChangeAsync(
            Core.Session.VaultSessionLease restrictedSession,
            string newPassword,
            string? hint,
            CancellationToken ct = default)
        {
            RequiredPasswordChangeCall = (restrictedSession, newPassword, hint);
            return Task.FromResult(RequiredPasswordChangeResult);
        }
        public Task<AuthResult> UnlockAsync(Core.Session.AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default) => UnlockAsync(masterPassword, ct);
        public Task<AuthResult> UnlockWithPinAsync(Core.Session.AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default) => UnlockWithPinAsync(pin, ct);
        public Task<AuthResult> UnlockWithKeyConnectorAsync(Core.Session.AuthenticationAttemptLease attempt, CancellationToken ct = default)
        {
            UnlockWithKeyConnectorCalled = true;
            return Task.FromResult(UnlockWithKeyConnectorResult);
        }
        public Task<AuthResult> UnlockWithHelloAsync(Core.Session.AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default) => UnlockWithHelloAsync(signature, ct);
        public Task<AuthResult> CompleteTrustedDeviceApprovalAsync(
            Core.Session.VaultSessionLease authenticatedSession,
            byte[] userKey,
            bool trustCurrentDevice,
            CancellationToken ct = default)
        {
            TrustCurrentDeviceAfterApproval = trustCurrentDevice;
            ApprovedUserKeyLength = userKey.Length;
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(userKey);
            return Task.FromResult(TrustedDeviceApprovalResult);
        }

        public Task<AuthResult> CompleteTdeV2JitProvisioningAsync(
            Core.Session.VaultSessionLease authenticatedSession,
            string organizationIdentifier,
            bool trustCurrentDevice,
            CancellationToken ct = default)
        {
            TdeV2JitOrganizationIdentifier = organizationIdentifier;
            TrustCurrentDeviceAfterJit = trustCurrentDevice;
            return Task.FromResult(TdeV2JitResult);
        }

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

    private sealed class FakeDemoVaultSessionService : IDemoVaultSessionService
    {
        public bool Opened { get; private set; }

        public Task<Core.Session.VaultSessionLease> OpenDemoVaultAsync(CancellationToken ct = default)
        {
            Opened = true;
            return Task.FromResult(TestSessionLeases.CreateSessionLease());
        }
    }

    private static PersistedSession SavedSession(string accountId, string serverUrl) => new(
        PersistedSession.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        $"device-{accountId}",
        $"refresh-{accountId}",
        $"key-{accountId}",
        KdfType.Pbkdf2,
        600_000,
        null,
        null);
}
