using Api;
using App.Services;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;
// DataPageState, UiOperationStatus are in App.Services namespace

namespace App.Tests;

// ============================================================================
// 2FA SettingsViewModel 测试（Task 6 TDD RED 先写）
// ============================================================================

/// <summary>
/// TDD: SettingsViewModel 的 2FA 命令测试。
/// 链：VM → TwoFactorUiService（真实）→ FakeTwoFactorService
/// </summary>
[Collection(TestCollectionNames.AppPreferences)]
public class SettingsViewModelTwoFactorTests : AppPreferencesTestBase
{
    // -----------------------------------------------------------------------
    // FakeTwoFactorService：记录调用并可控抛出
    // -----------------------------------------------------------------------
    private sealed class FakeTwoFactorService : ITwoFactorService
    {
        public bool ThrowOnList { get; set; }
        public bool ThrowOnBeginTotp { get; set; }
        public bool ThrowOnEnableTotp { get; set; }
        public bool ThrowOnGetRecoveryCode { get; set; }
        public bool ThrowOnBeginEmail { get; set; }
        public bool ThrowOnSendEmail { get; set; }
        public bool ThrowOnEnableEmail { get; set; }
        public bool ThrowOnDisable { get; set; }
        public bool ThrowCancelOnDisable { get; set; }
        public Exception? Failure { get; set; }
        public (string secret, string otpauth) TotpSetup { get; set; } =
            ("TOTPSECRET", "otpauth://totp/test");
        public TotpActivationResult ActivationResult { get; set; } =
            TotpActivationResult.EnabledWithRecoveryCode("RECOVERY-CODE-123");
        public string RecoveryCode { get; set; } = "RECOVERY-CODE-123";

        public string? LastPw { get; private set; }
        public (string pw, string secret, string code)? LastEnableTotp { get; private set; }
        public int EnableTotpCalls { get; private set; }
        public string? LastRecoveryPassword { get; private set; }
        public (string pw, string email, string token)? LastEnableEmail { get; private set; }
        public (string pw, int type)? LastDisable { get; private set; }

        public IReadOnlyList<TwoFactorProvider> Providers { get; set; } =
        [
            new TwoFactorProvider(0, true),
            new TwoFactorProvider(1, false),
        ];

        public Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnList) throw new TwoFactorOperationException("list failed");
            return Task.FromResult(Providers);
        }

        public Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnBeginTotp) throw new TwoFactorOperationException("begin totp failed");
            LastPw = pw;
            return Task.FromResult(TotpSetup);
        }

        public Task<TotpActivationResult> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnEnableTotp) throw new TwoFactorOperationException("enable totp failed");
            EnableTotpCalls++;
            LastEnableTotp = (pw, secret, code);
            return Task.FromResult(ActivationResult);
        }

        public Task<string> GetRecoveryCodeAsync(string pw, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnGetRecoveryCode) throw new TwoFactorOperationException("recovery read failed");
            LastRecoveryPassword = pw;
            return Task.FromResult(RecoveryCode);
        }

        public Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnBeginEmail) throw new TwoFactorOperationException("begin email failed");
            LastPw = pw;
            return Task.FromResult<string?>("t***@example.com");
        }

        public Task SendEmailAsync(string pw, string email, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnSendEmail) throw new TwoFactorOperationException("send email failed");
            LastPw = pw;
            return Task.CompletedTask;
        }

        public Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnEnableEmail) throw new TwoFactorOperationException("enable email failed");
            LastEnableEmail = (pw, email, token);
            return Task.CompletedTask;
        }

        public Task DisableAsync(string pw, int type, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowCancelOnDisable)
                throw new OperationCanceledException(
                    "user cancelled",
                    new CancellationToken(canceled: true));
            if (ThrowOnDisable) throw new TwoFactorOperationException("disable failed");
            LastDisable = (pw, type);
            return Task.CompletedTask;
        }
    }

    // -----------------------------------------------------------------------
    // 辅助工厂：VM → TwoFactorUiService（真实）→ FakeTwoFactorService
    // -----------------------------------------------------------------------
    private static (SettingsViewModel vm, FakeTwoFactorService svc) Build2Fa(
        bool throwOnList = false,
        bool throwOnBeginTotp = false,
        bool throwOnEnableTotp = false,
        bool throwOnBeginEmail = false,
        bool throwOnSendEmail = false,
        bool throwOnEnableEmail = false,
        bool throwOnDisable = false)
    {
        var svc = new FakeTwoFactorService
        {
            ThrowOnList = throwOnList,
            ThrowOnBeginTotp = throwOnBeginTotp,
            ThrowOnEnableTotp = throwOnEnableTotp,
            ThrowOnBeginEmail = throwOnBeginEmail,
            ThrowOnSendEmail = throwOnSendEmail,
            ThrowOnEnableEmail = throwOnEnableEmail,
            ThrowOnDisable = throwOnDisable,
        };
        var ui = new TwoFactorUiService(svc);
        return (new SettingsViewModel(twoFactorUi: ui), svc);
    }

    // -----------------------------------------------------------------------
    // LoadProvidersAsync: 成功空列表 → Succeeded，非 Failed
    // -----------------------------------------------------------------------
    [Fact]
    public async Task LoadProviders_SuccessEmpty_IsSucceededNotFailed()
    {
        var service = new FakeTwoFactorService { Providers = [] };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.LoadProvidersAsync();

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value);
        Assert.Equal(DataPageState.Empty, vm.TwoFactorState);
    }

    // -----------------------------------------------------------------------
    // LoadProvidersAsync: 服务失败 → Failed, Value=null，非 Succeeded+empty
    // -----------------------------------------------------------------------
    [Fact]
    public async Task LoadProviders_ServiceFailure_IsFailedNotEmptySuccess()
    {
        var (vm, _) = Build2Fa(throwOnList: true);

        var result = await vm.LoadProvidersAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(DataPageState.Error, vm.TwoFactorState);
    }

    [Fact]
    public async Task LoadProviders_RequestTimeout_ReturnsFailedAndShowsTimeout()
    {
        var service = new FakeTwoFactorService { Failure = RequestTimeout() };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.LoadProvidersAsync(CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, result.Error);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.OperationError);
        Assert.Equal(DataPageState.Error, vm.TwoFactorState);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // LoadProvidersAsync: IsBusy → Busy，非空集合成功
    // -----------------------------------------------------------------------
    [Fact]
    public async Task LoadProviders_WhenSettingsBusy_ReturnsBusy()
    {
        var (vm, _) = Build2Fa();
        vm.IsBusy = true;

        var result = await vm.LoadProvidersAsync();

        Assert.Equal(UiOperationStatus.Busy, result.Status);
        Assert.Null(result.Value);
    }

    // -----------------------------------------------------------------------
    // LoadProvidersAsync: 成功有内容 → Succeeded + Content state
    // -----------------------------------------------------------------------
    [Fact]
    public async Task LoadProviders_SuccessWithItems_IsSucceededContent()
    {
        var (vm, _) = Build2Fa();

        var result = await vm.LoadProvidersAsync();

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(DataPageState.Content, vm.TwoFactorState);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // BeginTotpSetupAsync: 成功返回 (secret, otpauth)，清空 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task BeginTotpSetup_Success_ReturnsSecretAndClearsError()
    {
        var (vm, _) = Build2Fa();
        vm.OperationError = "stale";

        var result = await vm.BeginTotpSetupAsync("master-pw");

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal("TOTPSECRET", result.Value.secret);
        Assert.Equal("otpauth://totp/test", result.Value.otpauth);
    }

    // -----------------------------------------------------------------------
    // BeginTotpSetupAsync: 服务抛出时写 OperationError，返回默认 tuple
    // -----------------------------------------------------------------------
    [Fact]
    public async Task BeginTotpSetup_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnBeginTotp: true);

        var result = await vm.BeginTotpSetupAsync("master-pw");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task BeginTotpSetup_RequestTimeout_SetsSafeTimeoutError()
    {
        var service = new FakeTwoFactorService { Failure = RequestTimeout() };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.BeginTotpSetupAsync("master-pw", CancellationToken.None);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // EnableTotpAsync: 成功返回 RecoveryCode，清空 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task EnableTotp_Success_ReturnsRecoveryCode()
    {
        var (vm, svc) = Build2Fa();
        vm.OperationError = "stale";

        var result = await vm.EnableTotpAsync("pw", "TOTPSECRET", "123456");

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.True(result.Value?.IsEnabled);
        Assert.True(result.Value?.HasRecoveryCode);
        Assert.Equal("RECOVERY-CODE-123", result.Value?.RecoveryCode);
        Assert.Equal(("pw", "TOTPSECRET", "123456"), svc.LastEnableTotp);
    }

    // -----------------------------------------------------------------------
    // EnableTotpAsync: 服务抛出时写 OperationError，返回空字符串
    // -----------------------------------------------------------------------
    [Fact]
    public async Task EnableTotp_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnEnableTotp: true);

        var result = await vm.EnableTotpAsync("pw", "secret", "code");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // SendEmailAsync: 成功时清空 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task SendEmail_Success_ClearsOperationError()
    {
        var (vm, svc) = Build2Fa();
        vm.OperationError = "stale";

        var result = await vm.SendEmailAsync("pw", "user@example.com");

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal("pw", svc.LastPw);
    }

    // -----------------------------------------------------------------------
    // SendEmailAsync: 服务抛出时写 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task SendEmail_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnSendEmail: true);

        var result = await vm.SendEmailAsync("pw", "user@example.com");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // EnableEmailAsync: 成功时清空 OperationError，委托到服务
    // -----------------------------------------------------------------------
    [Fact]
    public async Task EnableEmail_Success_ClearsOperationError()
    {
        var (vm, svc) = Build2Fa();
        vm.OperationError = "stale";

        var result = await vm.EnableEmailAsync("pw", "user@example.com", "TOKEN123");

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal(("pw", "user@example.com", "TOKEN123"), svc.LastEnableEmail);
    }

    // -----------------------------------------------------------------------
    // EnableEmailAsync: 服务抛出时写 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task EnableEmail_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnEnableEmail: true);

        var result = await vm.EnableEmailAsync("pw", "user@example.com", "TOKEN");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // DisableTwoFactorAsync: 成功时清空 OperationError，委托到服务
    // -----------------------------------------------------------------------
    [Fact]
    public async Task DisableTwoFactor_Success_ClearsOperationError()
    {
        var (vm, svc) = Build2Fa();
        vm.OperationError = "stale";

        var result = await vm.DisableTwoFactorAsync("pw", 0);

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal(("pw", 0), svc.LastDisable);
    }

    // -----------------------------------------------------------------------
    // DisableTwoFactorAsync: 服务抛出时写 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task DisableTwoFactor_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnDisable: true);

        var result = await vm.DisableTwoFactorAsync("pw", 0);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // DisableTwoFactorAsync: OperationCanceledException 不设 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task DisableTwoFactor_Cancelled_DoesNotSetOperationError()
    {
        var svc = new FakeTwoFactorService { ThrowCancelOnDisable = true };
        var ui = new TwoFactorUiService(svc);
        var vm = new SettingsViewModel(twoFactorUi: ui);

        var result = await vm.DisableTwoFactorAsync("pw", 0);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // 无参构造（null _twoFactorUi）：各方法应设 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ListProviders_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.LoadProvidersAsync();

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task BeginTotpSetup_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.BeginTotpSetupAsync("pw");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
    }

    [Fact]
    public async Task EnableTotp_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.EnableTotpAsync("pw", "secret", "code");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
    }

    [Fact]
    public async Task DisableTwoFactor_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.DisableTwoFactorAsync("pw", 0);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
    }

    [Fact]
    public async Task SendEmail_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.SendEmailAsync("pw", "user@example.com");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task EnableEmail_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.EnableEmailAsync("pw", "user@example.com", "TOKEN");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task BeginTotpSetup_WhenBusy_ReturnsBusyWithoutAdvancing()
    {
        var (vm, svc) = Build2Fa();
        vm.IsBusy = true;

        var result = await vm.BeginTotpSetupAsync("pw");

        Assert.Equal(UiOperationStatus.Busy, result.Status);
        Assert.Null(svc.LastPw);
    }

    [Fact]
    public async Task SendEmail_WhenBusy_ReturnsBusyWithoutCallingService()
    {
        var (vm, svc) = Build2Fa();
        vm.IsBusy = true;

        var result = await vm.SendEmailAsync("pw", "user@example.com");

        Assert.Equal(UiOperationStatus.Busy, result.Status);
        Assert.Null(svc.LastPw);
    }

    [Fact]
    public async Task BeginTotpSetup_WhenCancelled_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new FakeTwoFactorService
        {
            Failure = new OperationCanceledException(cts.Token),
        };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.BeginTotpSetupAsync("pw", cts.Token);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("", "otpauth://totp/test")]
    [InlineData("TOTPSECRET", "")]
    public async Task BeginTotpSetup_WithIncompletePayload_ReturnsFailed(
        string secret,
        string otpauth)
    {
        var service = new FakeTwoFactorService { TotpSetup = (secret, otpauth) };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.BeginTotpSetupAsync("pw");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task EnableTotp_WithoutRecoveryCode_ReturnsCommittedSuccess()
    {
        var service = new FakeTwoFactorService
        {
            ActivationResult = TotpActivationResult.EnabledWithoutRecoveryCode(),
        };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.EnableTotpAsync("pw", "secret", "123456");

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.True(result.Value?.IsEnabled);
        Assert.False(result.Value?.HasRecoveryCode);
        Assert.Null(result.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task GetRecoveryCode_Success_DoesNotEnableTotpAgain()
    {
        var service = new FakeTwoFactorService { RecoveryCode = "RETRIED-CODE" };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.GetRecoveryCodeAsync("recovery-password");

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal("RETRIED-CODE", result.Value);
        Assert.Equal("recovery-password", service.LastRecoveryPassword);
        Assert.Equal(0, service.EnableTotpCalls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task GetRecoveryCode_Failure_IsExplicitAndDoesNotEnableTotpAgain()
    {
        var service = new FakeTwoFactorService { ThrowOnGetRecoveryCode = true };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.GetRecoveryCodeAsync("recovery-password");

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(0, service.EnableTotpCalls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task GetRecoveryCode_WhenBusy_ReturnsBusyWithoutCallingService()
    {
        var service = new FakeTwoFactorService();
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service))
        {
            IsBusy = true,
        };

        var result = await vm.GetRecoveryCodeAsync("recovery-password");

        Assert.Equal(UiOperationStatus.Busy, result.Status);
        Assert.Null(service.LastRecoveryPassword);
        Assert.Equal(0, service.EnableTotpCalls);
    }

    [Fact]
    public async Task GetRecoveryCode_WhenCancelled_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new FakeTwoFactorService
        {
            Failure = new OperationCanceledException(cts.Token),
        };
        var vm = new SettingsViewModel(
            twoFactorUi: new TwoFactorUiService(service));

        var result = await vm.GetRecoveryCodeAsync("recovery-password", cts.Token);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.Equal(0, service.EnableTotpCalls);
        Assert.False(vm.IsBusy);
    }

    private static TaskCanceledException RequestTimeout() => new(
        "request timed out",
        new RequestTimeoutException(
            RequestTimeoutPhase.ControlPlane,
            TimeSpan.FromSeconds(30)),
        CancellationToken.None);
}

/// <summary>
/// TDD: SettingsViewModel 的账户操作命令测试（Task 6）。
/// 验证范围：confirm 校验 → 错误，成功时清空错误，命令委托到 IAccountService。
/// 链：VM → AccountUiService（真实）→ FakeAccountService
/// </summary>
[Collection(TestCollectionNames.AppPreferences)]
public class SettingsViewModelTests : AppPreferencesTestBase
{
    // -----------------------------------------------------------------------
    // FakeVaultSnapshot / FakeVaultService
    // -----------------------------------------------------------------------
    private sealed class FakeVaultSnapshot : IVaultSnapshot
    {
        public VaultState State => VaultState.Unlocked;
        public IReadOnlyList<Cipher> Ciphers => [];
        public IReadOnlyList<Folder> Folders => [];
        public IReadOnlyList<DeviceInfo> Devices => [];
        public AccountInfo Account { get; } =
            new("test-account", "test@example.com", new ServerOrigin("https://vault.example.com"), "T", "PBKDF2 · 600000");
        public int SkippedCipherCount => 0;
        public bool DataStale => false;
        public long Generation => 0;
        public int SkippedFolderCount => 0;
    }

    private sealed class FakeVaultService : IVaultService
    {
        public IVaultSnapshot Snapshot { get; } = new FakeVaultSnapshot();
        public IReadOnlyList<Cipher> GetCiphers() => [];
        public IReadOnlyList<Folder> GetFolders() => [];
        public IReadOnlyList<DeviceInfo> GetDevices() => [];
        public VaultSessionLease CaptureLease() =>
            throw new InvalidOperationException("This fake has no vault session.");
        public bool IsCurrent(VaultSessionLease lease) => false;
        public void ThrowIfExpired(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
    }

    // -----------------------------------------------------------------------
    // FakeAccountService：记录调用并可控抛出
    // -----------------------------------------------------------------------
    private sealed class FakeAccountService : IAccountService
    {
        public string? LastName { get; private set; }
        public (string Current, string Next, string? Hint)? LastChangePassword { get; private set; }
        public (string Current, int Iterations)? LastChangeKdf { get; private set; }
        public bool ThrowOnRename { get; set; }
        public bool ThrowOnChangePassword { get; set; }
        public bool ThrowOnChangeKdf { get; set; }
        public bool ThrowCancelOnChangePassword { get; set; }
        public Exception? Failure { get; set; }

        public Task UpdateNameAsync(string name, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnRename)
                throw new AccountOperationException("rename failed");
            LastName = name;
            return Task.CompletedTask;
        }

        public Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default)
        {
            if (ThrowCancelOnChangePassword)
                throw new OperationCanceledException(
                    "user cancelled",
                    new CancellationToken(canceled: true));
            if (Failure is { } failure) throw failure;
            if (ThrowOnChangePassword)
                throw new AccountOperationException("change password failed");
            LastChangePassword = (currentPassword, newPassword, hint);
            return Task.CompletedTask;
        }

        public Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            if (ThrowOnChangeKdf)
                throw new AccountOperationException("change kdf failed");
            LastChangeKdf = (currentPassword, newIterations);
            return Task.CompletedTask;
        }

        public Task<bool> VerifyMasterPasswordAsync(string password, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    // -----------------------------------------------------------------------
    // 辅助工厂：VM → AccountUiService（真实）→ FakeAccountService
    // -----------------------------------------------------------------------
    private static (SettingsViewModel vm, FakeAccountService svc) Build(
        bool throwOnRename = false,
        bool throwOnChangePwd = false,
        bool throwOnChangeKdf = false,
        IAppLifecycleCoordinator? lifecycle = null)
    {
        var svc = new FakeAccountService
        {
            ThrowOnRename = throwOnRename,
            ThrowOnChangePassword = throwOnChangePwd,
            ThrowOnChangeKdf = throwOnChangeKdf,
        };
        var vault = new FakeVaultService();
        var ui = new AccountUiService(vault, svc);
        return (new SettingsViewModel(ui, lifecycle), svc);
    }

    // Task 2:改密/改 KDF 成功走生命周期协调器 CredentialChanged;失败零次。
    private sealed class RecordingLifecycle : IAppLifecycleCoordinator
    {
        public List<AppLifecycleIntent> Requests { get; } = new();
        public bool IsTransitioning => false;
        public Task RequestAsync(
            AppLifecycleIntent intent,
            Core.Session.VaultSessionLease? expectedSession = null)
        {
            Requests.Add(intent);
            return Task.CompletedTask;
        }
        public bool MarkSessionReady() => true;
        public void FailClosedSynchronously() { }
    }

    [Fact]
    public async Task ChangePassword_Success_RequestsCredentialChangedOnce()
    {
        var lifecycle = new RecordingLifecycle();
        var (vm, _) = Build(lifecycle: lifecycle);

        await vm.ChangePasswordAsync("current", "newPwd", "newPwd", null);

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.Single(lifecycle.Requests);
        Assert.Equal(AppLifecycleIntent.CredentialChanged, lifecycle.Requests[0]);
    }

    [Fact]
    public async Task ChangePassword_Failure_RequestsNoLifecycleIntent()
    {
        var lifecycle = new RecordingLifecycle();
        var (vm, _) = Build(throwOnChangePwd: true, lifecycle: lifecycle);

        await vm.ChangePasswordAsync("current", "newPwd", "newPwd", null);

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.Empty(lifecycle.Requests);
    }

    [Fact]
    public async Task ChangeIterations_Success_RequestsCredentialChangedOnce()
    {
        var lifecycle = new RecordingLifecycle();
        var (vm, _) = Build(lifecycle: lifecycle);

        await vm.ChangeIterationsAsync("current", 600000);

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.Single(lifecycle.Requests);
        Assert.Equal(AppLifecycleIntent.CredentialChanged, lifecycle.Requests[0]);
    }

    [Fact]
    public async Task ChangeIterations_Failure_RequestsNoLifecycleIntent()
    {
        var lifecycle = new RecordingLifecycle();
        var (vm, _) = Build(throwOnChangeKdf: true, lifecycle: lifecycle);

        await vm.ChangeIterationsAsync("current", 600000);

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.Empty(lifecycle.Requests);
    }

    // -----------------------------------------------------------------------
    // OperationError 初始为空
    // -----------------------------------------------------------------------
    [Fact]
    public void OperationError_DefaultEmpty()
    {
        var (vm, _) = Build();
        Assert.Equal(string.Empty, vm.OperationError);
    }

    // -----------------------------------------------------------------------
    // IsBusy 初始为 false
    // -----------------------------------------------------------------------
    [Fact]
    public void IsBusy_DefaultFalse()
    {
        var (vm, _) = Build();
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // ChangePassword: confirm 与 new 不匹配时写 OperationError
    // （校验由真实 AccountUiService 执行）
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangePassword_ConfirmMismatch_SetsOperationError()
    {
        var (vm, _) = Build();

        await vm.ChangePasswordAsync("current", "newPwd", "differentPwd", null);

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // ChangePassword: new 为空时写 OperationError
    // （校验由真实 AccountUiService 执行）
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangePassword_EmptyNewPassword_SetsOperationError()
    {
        var (vm, _) = Build();

        await vm.ChangePasswordAsync("current", "", "", null);

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // ChangePassword: 成功时清空 OperationError，委托到 FakeAccountService
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangePassword_Success_ClearsOperationError()
    {
        var (vm, svc) = Build();
        // 先设置一个旧错误
        vm.OperationError = "stale error";

        await vm.ChangePasswordAsync("current", "newPwd", "newPwd", null);

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.NotNull(svc.LastChangePassword);
        Assert.Equal("newPwd", svc.LastChangePassword!.Value.Next);
    }

    // -----------------------------------------------------------------------
    // ChangePassword: 服务抛出时写 OperationError，IsBusy 归 false
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangePassword_ServiceThrows_SetsOperationErrorAndClearsBusy()
    {
        var (vm, _) = Build(throwOnChangePwd: true);

        await vm.ChangePasswordAsync("current", "newPwd", "newPwd", null);

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // Rename: 成功时清空 OperationError，委托到 FakeAccountService
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Rename_Success_ClearsOperationErrorAndDelegatesToService()
    {
        var (vm, svc) = Build();
        vm.OperationError = "stale";

        await vm.RenameAsync("Alice");

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal("Alice", svc.LastName);
    }

    // -----------------------------------------------------------------------
    // Rename: 服务抛出时写 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Rename_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build(throwOnRename: true);

        await vm.RenameAsync("Alice");

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Rename_RequestTimeout_SetsSafeTimeoutError()
    {
        var svc = new FakeAccountService { Failure = RequestTimeout() };
        var vm = new SettingsViewModel(
            new AccountUiService(new FakeVaultService(), svc));

        await vm.RenameAsync("Alice", CancellationToken.None);

        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // ChangeIterations: 成功时清空 OperationError，委托到 FakeAccountService
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangeIterations_Success_ClearsOperationErrorAndDelegatesToService()
    {
        var (vm, svc) = Build();
        vm.OperationError = "stale";

        await vm.ChangeIterationsAsync("current", 600000);

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.NotNull(svc.LastChangeKdf);
        Assert.Equal(600000, svc.LastChangeKdf!.Value.Iterations);
    }

    // -----------------------------------------------------------------------
    // ChangeIterations: 服务抛出时写 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangeIterations_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build(throwOnChangeKdf: true);

        await vm.ChangeIterationsAsync("current", 600000);

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // _accountUi 为 null 时各路径应设 OperationError（而非静默返回）
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangePassword_NullAccountUi_SetsOperationError()
    {
        var vm = new SettingsViewModel(); // 无参构造，_accountUi == null

        await vm.ChangePasswordAsync("current", "newPwd", "newPwd", null);

        Assert.NotEqual(string.Empty, vm.OperationError);
    }

    [Fact]
    public async Task Rename_NullAccountUi_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        await vm.RenameAsync("Alice");

        Assert.NotEqual(string.Empty, vm.OperationError);
    }

    [Fact]
    public async Task ChangeIterations_NullAccountUi_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        await vm.ChangeIterationsAsync("current", 600000);

        Assert.NotEqual(string.Empty, vm.OperationError);
    }

    // -----------------------------------------------------------------------
    // OperationCanceledException 不应设 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ChangePassword_Cancelled_DoesNotSetOperationError()
    {
        var svc = new FakeAccountService { ThrowCancelOnChangePassword = true };
        var vault = new FakeVaultService();
        var ui = new AccountUiService(vault, svc);
        var vm = new SettingsViewModel(ui);

        var result = await vm.ChangePasswordAsync("current", "newPwd", "newPwd", null);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Rename_WhenBusy_ReturnsBusyWithoutCallingService()
    {
        var (vm, service) = Build();
        vm.IsBusy = true;

        var result = await vm.RenameAsync("Alice");

        Assert.Equal(UiOperationStatus.Busy, result.Status);
        Assert.Null(service.LastName);
    }

    private static TaskCanceledException RequestTimeout() => new(
        "request timed out",
        new RequestTimeoutException(
            RequestTimeoutPhase.ControlPlane,
            TimeSpan.FromSeconds(30)),
        CancellationToken.None);
}
