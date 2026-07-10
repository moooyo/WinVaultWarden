using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public sealed class SensitiveViewModelClearTests
{
    [Fact]
    public void VaultClear_DropsDetailDraftCollectionsAndSelection()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.SelectedItem = vm.Items.First(item => item.Id == "1");
        vm.BeginEdit("1");

        ((ISensitiveState)vm).ClearSensitive();

        Assert.Null(vm.Detail);
        Assert.Null(vm.EditorDraft);
        Assert.Null(vm.SelectedItem);
        Assert.Empty(vm.Items);
        Assert.Empty(vm.FilteredItems);
        Assert.Empty(vm.GroupedItems);
        Assert.Equal(string.Empty, vm.SearchText);
    }

    [Fact]
    public void LoginAndRegisterClear_AllSecretInputs()
    {
        var login = new LoginViewModel(new NoopAuth())
        {
            MasterPassword = "master",
            Pin = "123456",
            TwoFactorCode = "654321",
        };
        var register = new RegisterViewModel
        {
            MasterPassword = "master",
            ConfirmPassword = "master",
            MasterPasswordHint = "hint",
        };

        ((ISensitiveState)login).ClearSensitive();
        ((ISensitiveState)register).ClearSensitive();

        Assert.Equal(string.Empty, login.MasterPassword);
        Assert.Equal(string.Empty, login.Pin);
        Assert.Equal(string.Empty, login.TwoFactorCode);
        Assert.Equal(string.Empty, register.MasterPassword);
        Assert.Equal(string.Empty, register.ConfirmPassword);
        Assert.Null(register.MasterPasswordHint);
    }

    private sealed class NoopAuth : Core.Services.IAuthService
    {
        public Task<Core.Services.AuthResult> LoginAsync(string serverUrl, string email, string masterPassword, CancellationToken ct = default) =>
            Task.FromResult<Core.Services.AuthResult>(new Core.Services.AuthResult.Failure(""));
        public Task<Core.Services.AuthResult> SubmitTwoFactorAsync(string code, int provider, CancellationToken ct = default, bool rememberDevice = true) =>
            Task.FromResult<Core.Services.AuthResult>(new Core.Services.AuthResult.Failure(""));
        public Task<bool> ResendTwoFactorEmailAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<Core.Services.AuthResult> UnlockAsync(string masterPassword, CancellationToken ct = default) =>
            Task.FromResult<Core.Services.AuthResult>(new Core.Services.AuthResult.Failure(""));
        public Task<Core.Services.AuthResult> UnlockWithPinAsync(string pin, CancellationToken ct = default) =>
            Task.FromResult<Core.Services.AuthResult>(new Core.Services.AuthResult.Failure(""));
        public Task<Core.Services.AuthResult> UnlockWithHelloAsync(byte[] signature, CancellationToken ct = default) =>
            Task.FromResult<Core.Services.AuthResult>(new Core.Services.AuthResult.Failure(""));
        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
