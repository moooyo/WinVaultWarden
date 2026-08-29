using App.Services;
using App.ViewModels;
using Core.Abstractions;
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
    public async Task SwitchAccountCommand_UsesLifecycleCoordinator()
    {
        var auth = new FakeAuthService();
        var lifecycle = new RecordingLifecycle();
        var vm = new LoginViewModel(auth, null, lifecycle: lifecycle);
        vm.PrepareUnlock("https://vault.example", "me@example.com");

        await vm.SwitchAccountCommand.ExecuteAsync(null);

        // 切换账户只经协调器请求 Logout intent;不再旁路直接 auth.LogoutAsync。
        Assert.Equal(AppLifecycleIntent.Logout, lifecycle.Requested);
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

    [Fact]
    public async Task TypedNetworkFailure_ShowsStableLocalizedMessage()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.Failure(
                "sensitive endpoint detail",
                AuthFailureKind.Network),
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

        Assert.Equal("无法连接服务器，请检查网络后重试。", vm.Status);
        Assert.DoesNotContain("sensitive endpoint detail", vm.Status);
        Assert.Equal(LoginStatusKind.Error, vm.StatusKind);
    }

    [Fact]
    public async Task TwoFactorNetworkFailure_PreservesAttemptAndCanRetry()
    {
        var auth = new FakeAuthService
        {
            LoginResult = new AuthResult.TwoFactorRequired([0]),
            SubmitResult = new AuthResult.Failure(
                "sensitive endpoint detail",
                AuthFailureKind.Network),
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
        Assert.Equal("无法连接服务器，请检查网络后重试。", vm.Status);
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

    private sealed class RecordingLifecycle : IAppLifecycleCoordinator
    {
        public bool IsTransitioning => false;
        public AppLifecycleIntent? Requested { get; private set; }
        public Task RequestAsync(AppLifecycleIntent intent, Core.Session.VaultSessionLease? expectedSession = null)
        {
            Requested = intent;
            return Task.CompletedTask;
        }
        public bool MarkSessionReady() => true;
        public void FailClosedSynchronously() { }
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

    private sealed class FakeAuthService : IAuthService
    {
        private readonly Vault.VaultSession _attempts = new();
        public AuthResult LoginResult { get; set; } = new AuthResult.Failure("not set");
        public AuthResult SubmitResult { get; set; } = new AuthResult.Failure("not set");
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
        public int AbandonCalls { get; private set; }

        public Core.Session.AuthenticationAttemptLease BeginAuthenticationAttempt() => _attempts.BeginAuthenticationAttempt();
        public void AbandonAuthenticationAttempt(Core.Session.AuthenticationAttemptLease attempt)
        {
            AbandonCalls++;
            _attempts.AbandonAuthenticationAttempt(attempt);
        }
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
}
