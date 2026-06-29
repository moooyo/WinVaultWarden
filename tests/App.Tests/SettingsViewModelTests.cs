using App.Services;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

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
