using App.Services;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

// ============================================================================
// 2FA SettingsViewModel 测试（Task 6 TDD RED 先写）
// ============================================================================

/// <summary>
/// TDD: SettingsViewModel 的 2FA 命令测试。
/// 链：VM → TwoFactorUiService（真实）→ FakeTwoFactorService
/// </summary>
public class SettingsViewModelTwoFactorTests
{
    // -----------------------------------------------------------------------
    // FakeTwoFactorService：记录调用并可控抛出
    // -----------------------------------------------------------------------
    private sealed class FakeTwoFactorService : ITwoFactorService
    {
        public bool ThrowOnList { get; set; }
        public bool ThrowOnBeginTotp { get; set; }
        public bool ThrowOnEnableTotp { get; set; }
        public bool ThrowOnBeginEmail { get; set; }
        public bool ThrowOnSendEmail { get; set; }
        public bool ThrowOnEnableEmail { get; set; }
        public bool ThrowOnDisable { get; set; }
        public bool ThrowCancelOnDisable { get; set; }

        public string? LastPw { get; private set; }
        public (string pw, string secret, string code)? LastEnableTotp { get; private set; }
        public (string pw, string email, string token)? LastEnableEmail { get; private set; }
        public (string pw, int type)? LastDisable { get; private set; }

        public Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default)
        {
            if (ThrowOnList) throw new TwoFactorOperationException("list failed");
            return Task.FromResult<IReadOnlyList<TwoFactorProvider>>(
                [new TwoFactorProvider(0, true), new TwoFactorProvider(1, false)]);
        }

        public Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default)
        {
            if (ThrowOnBeginTotp) throw new TwoFactorOperationException("begin totp failed");
            LastPw = pw;
            return Task.FromResult(("TOTPSECRET", "otpauth://totp/test"));
        }

        public Task<string> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default)
        {
            if (ThrowOnEnableTotp) throw new TwoFactorOperationException("enable totp failed");
            LastEnableTotp = (pw, secret, code);
            return Task.FromResult("RECOVERY-CODE-123");
        }

        public Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default)
        {
            if (ThrowOnBeginEmail) throw new TwoFactorOperationException("begin email failed");
            LastPw = pw;
            return Task.FromResult<string?>("t***@example.com");
        }

        public Task SendEmailAsync(string pw, string email, CancellationToken ct = default)
        {
            if (ThrowOnSendEmail) throw new TwoFactorOperationException("send email failed");
            LastPw = pw;
            return Task.CompletedTask;
        }

        public Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
        {
            if (ThrowOnEnableEmail) throw new TwoFactorOperationException("enable email failed");
            LastEnableEmail = (pw, email, token);
            return Task.CompletedTask;
        }

        public Task DisableAsync(string pw, int type, CancellationToken ct = default)
        {
            if (ThrowCancelOnDisable) throw new OperationCanceledException("user cancelled");
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
    // ListProvidersAsync: 成功返回提供者列表，清空 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ListProviders_Success_ReturnsListAndClearsError()
    {
        var (vm, _) = Build2Fa();
        vm.OperationError = "stale";

        var result = await vm.ListProvidersAsync();

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal(2, result.Count);
        Assert.True(result[0].Enabled);
    }

    // -----------------------------------------------------------------------
    // ListProvidersAsync: 服务抛出时写 OperationError，返回空列表
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ListProviders_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnList: true);

        var result = await vm.ListProvidersAsync();

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Empty(result);
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

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal("TOTPSECRET", result.secret);
        Assert.Equal("otpauth://totp/test", result.otpauth);
    }

    // -----------------------------------------------------------------------
    // BeginTotpSetupAsync: 服务抛出时写 OperationError，返回默认 tuple
    // -----------------------------------------------------------------------
    [Fact]
    public async Task BeginTotpSetup_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnBeginTotp: true);

        var result = await vm.BeginTotpSetupAsync("master-pw");

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, result.secret);
    }

    // -----------------------------------------------------------------------
    // EnableTotpAsync: 成功返回 RecoveryCode，清空 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task EnableTotp_Success_ReturnsRecoveryCode()
    {
        var (vm, svc) = Build2Fa();
        vm.OperationError = "stale";

        var recovery = await vm.EnableTotpAsync("pw", "TOTPSECRET", "123456");

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal("RECOVERY-CODE-123", recovery);
        Assert.Equal(("pw", "TOTPSECRET", "123456"), svc.LastEnableTotp);
    }

    // -----------------------------------------------------------------------
    // EnableTotpAsync: 服务抛出时写 OperationError，返回空字符串
    // -----------------------------------------------------------------------
    [Fact]
    public async Task EnableTotp_ServiceThrows_SetsOperationError()
    {
        var (vm, _) = Build2Fa(throwOnEnableTotp: true);

        var recovery = await vm.EnableTotpAsync("pw", "secret", "code");

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, recovery);
    }

    // -----------------------------------------------------------------------
    // SendEmailAsync: 成功时清空 OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task SendEmail_Success_ClearsOperationError()
    {
        var (vm, svc) = Build2Fa();
        vm.OperationError = "stale";

        await vm.SendEmailAsync("pw", "user@example.com");

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

        await vm.SendEmailAsync("pw", "user@example.com");

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

        await vm.EnableEmailAsync("pw", "user@example.com", "TOKEN123");

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

        await vm.EnableEmailAsync("pw", "user@example.com", "TOKEN");

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

        await vm.DisableTwoFactorAsync("pw", 0);

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

        await vm.DisableTwoFactorAsync("pw", 0);

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

        await vm.DisableTwoFactorAsync("pw", 0);

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

        var result = await vm.ListProvidersAsync();

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.Empty(result);
    }

    [Fact]
    public async Task BeginTotpSetup_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.BeginTotpSetupAsync("pw");

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.Equal(string.Empty, result.secret);
    }

    [Fact]
    public async Task EnableTotp_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        var result = await vm.EnableTotpAsync("pw", "secret", "code");

        Assert.NotEqual(string.Empty, vm.OperationError);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task DisableTwoFactor_NullService_SetsOperationError()
    {
        var vm = new SettingsViewModel();

        await vm.DisableTwoFactorAsync("pw", 0);

        Assert.NotEqual(string.Empty, vm.OperationError);
    }
}

/// <summary>
/// TDD: SettingsViewModel 的账户操作命令测试（Task 6）。
/// 验证范围：confirm 校验 → 错误，成功时清空错误，命令委托到 IAccountService。
/// 链：VM → AccountUiService（真实）→ FakeAccountService
/// </summary>
public class SettingsViewModelTests
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
            new("test@example.com", "https://vault.example.com", "T", "PBKDF2 · 600000");
    }

    private sealed class FakeVaultService : IVaultService
    {
        public IVaultSnapshot Snapshot { get; } = new FakeVaultSnapshot();
        public IReadOnlyList<Cipher> GetCiphers() => [];
        public IReadOnlyList<Folder> GetFolders() => [];
        public IReadOnlyList<DeviceInfo> GetDevices() => [];
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

        public Task UpdateNameAsync(string name, CancellationToken ct = default)
        {
            if (ThrowOnRename)
                throw new AccountOperationException("rename failed");
            LastName = name;
            return Task.CompletedTask;
        }

        public Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default)
        {
            if (ThrowCancelOnChangePassword)
                throw new OperationCanceledException("user cancelled");
            if (ThrowOnChangePassword)
                throw new AccountOperationException("change password failed");
            LastChangePassword = (currentPassword, newPassword, hint);
            return Task.CompletedTask;
        }

        public Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default)
        {
            if (ThrowOnChangeKdf)
                throw new AccountOperationException("change kdf failed");
            LastChangeKdf = (currentPassword, newIterations);
            return Task.CompletedTask;
        }
    }

    // -----------------------------------------------------------------------
    // 辅助工厂：VM → AccountUiService（真实）→ FakeAccountService
    // -----------------------------------------------------------------------
    private static (SettingsViewModel vm, FakeAccountService svc) Build(
        bool throwOnRename = false,
        bool throwOnChangePwd = false,
        bool throwOnChangeKdf = false)
    {
        var svc = new FakeAccountService
        {
            ThrowOnRename = throwOnRename,
            ThrowOnChangePassword = throwOnChangePwd,
            ThrowOnChangeKdf = throwOnChangeKdf,
        };
        var vault = new FakeVaultService();
        var ui = new AccountUiService(vault, svc);
        return (new SettingsViewModel(ui), svc);
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

        await vm.ChangePasswordAsync("current", "newPwd", "newPwd", null);

        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);
    }
}
