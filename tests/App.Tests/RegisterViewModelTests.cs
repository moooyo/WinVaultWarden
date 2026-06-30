using App.Services;
using App.ViewModels;
using Core.Services;
using Xunit;

namespace App.Tests;

// ============================================================================
// RegisterViewModel 测试（TDD RED → GREEN）
// Task 5: App 层注册 UI 服务与视图模型
// 链：VM → RegisterUiService（真实）→ FakeRegisterService
// ============================================================================

public class RegisterViewModelTests
{
    // -----------------------------------------------------------------------
    // FakeRegisterService：记录最后一次调用参数，可控抛出 RegistrationException。
    // -----------------------------------------------------------------------
    private sealed class FakeRegisterService : IRegisterService
    {
        public bool Throw { get; set; }
        public string? ThrowMessage { get; set; }

        public string? LastServerUrl { get; private set; }
        public string? LastEmail { get; private set; }
        public string? LastName { get; private set; }
        public string? LastPassword { get; private set; }
        public string? LastHint { get; private set; }
        public int CallCount { get; private set; }

        public Task RegisterAsync(string serverUrl, string email, string? name, string password, string? hint, CancellationToken ct = default)
        {
            CallCount++;
            if (Throw)
                throw new RegistrationException(ThrowMessage ?? "服务端注册失败");
            LastServerUrl = serverUrl;
            LastEmail = email;
            LastName = name;
            LastPassword = password;
            LastHint = hint;
            return Task.CompletedTask;
        }
    }

    // -----------------------------------------------------------------------
    // 辅助工厂
    // -----------------------------------------------------------------------
    private static (RegisterViewModel vm, FakeRegisterService svc) Build(
        bool serviceThrows = false,
        string? throwMessage = null)
    {
        var svc = new FakeRegisterService { Throw = serviceThrows, ThrowMessage = throwMessage };
        var ui = new RegisterUiService(svc);
        return (new RegisterViewModel(ui), svc);
    }

    // -----------------------------------------------------------------------
    // 初始状态
    // -----------------------------------------------------------------------
    [Fact]
    public void InitialState_IsBusyFalse_OperationErrorEmpty_RegisteredFalse()
    {
        var (vm, _) = Build();

        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.HasOperationError);
        Assert.False(vm.Registered);
    }

    [Fact]
    public void InitialState_SelectedServerOptionIndex_IsZero()
    {
        var (vm, _) = Build();
        Assert.Equal(0, vm.SelectedServerOptionIndex);
    }

    [Fact]
    public void ShowCustomServerUrl_WhenIndex2_IsTrue()
    {
        var (vm, _) = Build();
        vm.SelectedServerOptionIndex = 2;
        Assert.True(vm.ShowCustomServerUrl);
    }

    [Fact]
    public void ShowCustomServerUrl_WhenIndex0_IsFalse()
    {
        var (vm, _) = Build();
        vm.SelectedServerOptionIndex = 0;
        Assert.False(vm.ShowCustomServerUrl);
    }

    // -----------------------------------------------------------------------
    // 校验失败：密码确认不匹配 → OperationError，服务不被调用
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_ConfirmMismatch_SetsOperationError_NoServiceCall()
    {
        var (vm, svc) = Build();
        vm.Email = "user@example.com";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Different1!";

        await vm.RegisterAsync();

        Assert.NotEmpty(vm.OperationError);
        Assert.True(vm.HasOperationError);
        Assert.False(vm.IsBusy);
        Assert.False(vm.Registered);
        Assert.Equal(0, svc.CallCount);
    }

    // -----------------------------------------------------------------------
    // 校验失败：邮箱为空 → OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_EmptyEmail_SetsOperationError()
    {
        var (vm, svc) = Build();
        vm.Email = "";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";

        await vm.RegisterAsync();

        Assert.NotEmpty(vm.OperationError);
        Assert.Equal(0, svc.CallCount);
    }

    // -----------------------------------------------------------------------
    // 校验失败：邮箱不含 @ → OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_EmailWithoutAtSign_SetsOperationError()
    {
        var (vm, svc) = Build();
        vm.Email = "notanemail";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";

        await vm.RegisterAsync();

        Assert.NotEmpty(vm.OperationError);
        Assert.Equal(0, svc.CallCount);
    }

    // -----------------------------------------------------------------------
    // 校验失败：密码为空 → OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_EmptyPassword_SetsOperationError()
    {
        var (vm, svc) = Build();
        vm.Email = "user@example.com";
        vm.MasterPassword = "";
        vm.ConfirmPassword = "";

        await vm.RegisterAsync();

        Assert.NotEmpty(vm.OperationError);
        Assert.Equal(0, svc.CallCount);
    }

    // -----------------------------------------------------------------------
    // 校验失败：名字超过50字符 → OperationError
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_NameTooLong_SetsOperationError()
    {
        var (vm, svc) = Build();
        vm.Email = "user@example.com";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";
        vm.Name = new string('A', 51);

        await vm.RegisterAsync();

        Assert.NotEmpty(vm.OperationError);
        Assert.Equal(0, svc.CallCount);
    }

    // -----------------------------------------------------------------------
    // 成功：正确参数 → Registered = true，RegisteredEmail/RegisteredServerUrl 已设置
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_ValidInputs_DelegatesToServiceAndSetsRegistered()
    {
        var (vm, svc) = Build();
        vm.SelectedServerOptionIndex = 0; // US 服务器
        vm.Email = "  user@example.com  "; // 带空格，应 Trim
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";
        vm.MasterPasswordHint = "my hint";

        await vm.RegisterAsync();

        Assert.True(vm.Registered);
        Assert.Equal("user@example.com", vm.RegisteredEmail);
        Assert.Equal("https://vault.bitwarden.com", vm.RegisteredServerUrl);
        Assert.Equal(string.Empty, vm.OperationError);
        Assert.False(vm.IsBusy);

        // 确认服务收到正确参数
        Assert.Equal(1, svc.CallCount);
        Assert.Equal("https://vault.bitwarden.com", svc.LastServerUrl);
        Assert.Equal("user@example.com", svc.LastEmail);
        Assert.Equal("my hint", svc.LastHint);
    }

    // -----------------------------------------------------------------------
    // 成功：EU 服务器（index=1）解析正确
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_EuServer_ResolvesEuUrl()
    {
        var (vm, svc) = Build();
        vm.SelectedServerOptionIndex = 1; // EU
        vm.Email = "user@example.com";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";

        await vm.RegisterAsync();

        Assert.True(vm.Registered);
        Assert.Equal("https://vault.bitwarden.eu", svc.LastServerUrl);
        Assert.Equal("https://vault.bitwarden.eu", vm.RegisteredServerUrl);
    }

    // -----------------------------------------------------------------------
    // 成功：自定义服务器（index=2）使用 ServerUrl 属性
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_SelfHostServer_UsesServerUrl()
    {
        var (vm, svc) = Build();
        vm.SelectedServerOptionIndex = 2;
        vm.ServerUrl = "http://10.0.1.20:8080";
        vm.Email = "user@example.com";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";

        await vm.RegisterAsync();

        Assert.True(vm.Registered);
        Assert.Equal("http://10.0.1.20:8080", svc.LastServerUrl);
    }

    // -----------------------------------------------------------------------
    // 服务抛出 RegistrationException → OperationError = ex.Message
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_ServiceThrowsRegistrationException_SetsOperationError()
    {
        var (vm, _) = Build(serviceThrows: true, throwMessage: "邮箱已被注册");
        vm.Email = "user@example.com";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";

        await vm.RegisterAsync();

        Assert.Equal("邮箱已被注册", vm.OperationError);
        Assert.False(vm.Registered);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // null 服务（无参构造）→ OperationError（注册服务不可用）
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_NullService_SetsOperationError()
    {
        var vm = new RegisterViewModel(); // 无参构造
        vm.Email = "user@example.com";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";

        await vm.RegisterAsync();

        Assert.NotEmpty(vm.OperationError);
        Assert.False(vm.Registered);
        Assert.False(vm.IsBusy);
    }

    // -----------------------------------------------------------------------
    // IsBusy 期间再次调用 RegisterAsync 应直接返回（不开始新任务）
    // -----------------------------------------------------------------------
    [Fact]
    public async Task RegisterAsync_WhenAlreadyBusy_ReturnsImmediately()
    {
        // 使用一个会挂起的服务来模拟正在进行中的操作
        var tcs = new TaskCompletionSource<bool>();
        var hangService = new HangingRegisterService(tcs.Task);
        var ui = new RegisterUiService(hangService);
        var vm = new RegisterViewModel(ui);
        vm.Email = "user@example.com";
        vm.MasterPassword = "Password1!";
        vm.ConfirmPassword = "Password1!";

        // 启动第一次注册（不等待完成）
        var firstTask = vm.RegisterAsync();
        // 此时 IsBusy 应为 true
        Assert.True(vm.IsBusy);

        // 第二次调用应立即返回
        await vm.RegisterAsync();

        // 完成第一次
        tcs.SetResult(true);
        await firstTask;
    }

    private sealed class HangingRegisterService : IRegisterService
    {
        private readonly Task _hang;
        public HangingRegisterService(Task hang) => _hang = hang;

        public async Task RegisterAsync(string serverUrl, string email, string? name, string password, string? hint, CancellationToken ct = default)
        {
            await _hang;
        }
    }
}
