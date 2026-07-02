using App.Services;
using App.ViewModels;
using Core.Services;
using Xunit;

namespace App.Tests;

// ============================================================================
// LoginViewModel.PrepareAccount 测试
// 验证：服务器选项索引正确映射；Stage 回到 Account；Email 填充；
// MasterPassword 清空；自托管 URL 不被 placeholder 覆盖。
// ============================================================================

public class LoginViewModelPrepareAccountTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static LoginViewModel Build() => new(new FakeAuthService());

    // -----------------------------------------------------------------------
    // US url → index 0, Stage==Account, Email set, MasterPassword==""
    // -----------------------------------------------------------------------

    [Fact]
    public void PrepareAccount_UsUrl_SetsIndex0AndAccountStage()
    {
        var vm = Build();
        vm.MasterPassword = "old";

        vm.PrepareAccount("https://vault.bitwarden.com", "user@example.com");

        Assert.Equal(0, vm.SelectedServerOptionIndex);
        Assert.Equal(LoginStage.Account, vm.Stage);
        Assert.Equal("user@example.com", vm.Email);
        Assert.Equal(string.Empty, vm.MasterPassword);
    }

    [Fact]
    public void PrepareAccount_UsUrl_ServerUrlEqualsPassedUrl()
    {
        var vm = Build();
        vm.PrepareAccount("https://vault.bitwarden.com", "user@example.com");

        Assert.Equal("https://vault.bitwarden.com", vm.ServerUrl);
    }

    // -----------------------------------------------------------------------
    // EU url → index 1
    // -----------------------------------------------------------------------

    [Fact]
    public void PrepareAccount_EuUrl_SetsIndex1()
    {
        var vm = Build();

        vm.PrepareAccount("https://vault.bitwarden.eu", "eu@example.com");

        Assert.Equal(1, vm.SelectedServerOptionIndex);
        Assert.Equal(LoginStage.Account, vm.Stage);
        Assert.Equal("eu@example.com", vm.Email);
        Assert.Equal(string.Empty, vm.MasterPassword);
    }

    [Fact]
    public void PrepareAccount_EuUrl_ServerUrlEqualsPassedUrl()
    {
        var vm = Build();
        vm.PrepareAccount("https://vault.bitwarden.eu", "eu@example.com");

        Assert.Equal("https://vault.bitwarden.eu", vm.ServerUrl);
    }

    // -----------------------------------------------------------------------
    // Self-hosted url → index 2, ServerUrl must not be placeholder
    // -----------------------------------------------------------------------

    [Fact]
    public void PrepareAccount_SelfHostedUrl_SetsIndex2AndNoPlaceholderLeak()
    {
        var vm = Build();

        vm.PrepareAccount("http://10.0.1.20:8080", "test@winvaultwarden.local");

        Assert.Equal(2, vm.SelectedServerOptionIndex);
        // The real self-hosted URL must survive; placeholder must not leak.
        Assert.Equal("http://10.0.1.20:8080", vm.ServerUrl);
        Assert.NotEqual("https://vault.example.com", vm.ServerUrl);
    }

    [Fact]
    public void PrepareAccount_SelfHostedUrl_StageAndEmailCorrect()
    {
        var vm = Build();

        vm.PrepareAccount("http://10.0.1.20:8080", "test@winvaultwarden.local");

        Assert.Equal(LoginStage.Account, vm.Stage);
        Assert.Equal("test@winvaultwarden.local", vm.Email);
        Assert.Equal(string.Empty, vm.MasterPassword);
    }

    // -----------------------------------------------------------------------
    // Placeholder-leak regression: previously set US url then switch to custom
    // -----------------------------------------------------------------------

    [Fact]
    public void PrepareAccount_SelfHostedUrl_WhenPreviouslyOnUsSite_NoPlaceholderLeak()
    {
        var vm = Build();
        // Start on US (index 0) to make the later transition meaningful.
        vm.PrepareAccount("https://vault.bitwarden.com", "a@b.com");

        vm.PrepareAccount("http://10.0.1.20:8080", "test@winvaultwarden.local");

        Assert.Equal(2, vm.SelectedServerOptionIndex);
        Assert.Equal("http://10.0.1.20:8080", vm.ServerUrl);
    }

    // -----------------------------------------------------------------------
    // Status and TwoFactorCode are cleared
    // -----------------------------------------------------------------------

    [Fact]
    public void PrepareAccount_ClearsStatusAndTwoFactorCode()
    {
        var vm = Build();
        vm.Status = "some status";
        vm.TwoFactorCode = "123456";

        vm.PrepareAccount("https://vault.bitwarden.com", "a@b.com");

        Assert.Equal(string.Empty, vm.Status);
        Assert.Equal(string.Empty, vm.TwoFactorCode);
    }

    // -----------------------------------------------------------------------
    // Stub
    // -----------------------------------------------------------------------

    private sealed class FakeAuthService : IAuthService
    {
        public Task<AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default)
            => Task.FromResult<AuthResult>(new AuthResult.Success());

        public Task<AuthResult> SubmitTwoFactorAsync(string code, CancellationToken ct = default, bool rememberDevice = true)
            => Task.FromResult<AuthResult>(new AuthResult.Success());

        public Task<AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default)
            => Task.FromResult<AuthResult>(new AuthResult.Success());

        public Task<AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default)
            => Task.FromResult<AuthResult>(new AuthResult.Success());

        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
