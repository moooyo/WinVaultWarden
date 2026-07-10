using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public sealed class SensitiveViewModelClearTests
{
    [Fact]
    public async Task VaultClear_DropsDetailDraftCollectionsAndSelection()
    {
        var vm = new VaultViewModel(new MockVaultUiService());
        vm.SelectedItem = vm.Items.First(item => item.Id == "1");
        await vm.BeginEditAsync(
            "1",
            _ => Task.FromResult<string?>(null));

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

    [Fact]
    public async Task SendClear_DropsItemsAndReceivedSecrets()
    {
        var vm = new SendViewModel(new StubSendUiService());
        await vm.LoadAsync(CancellationToken.None);
        vm.ReceivedLinkUrl = "https://vault.example/#/send/acc/seed";
        vm.ReceivedLinkPassword = "secret";
        vm.ReceivedText = "payload";

        ((ISensitiveState)vm).ClearSensitive();

        Assert.Empty(vm.Items);
        Assert.Empty(vm.FilteredItems);
        Assert.Equal(string.Empty, vm.ReceivedLinkUrl);
        Assert.Equal(string.Empty, vm.ReceivedLinkPassword);
        Assert.Null(vm.ReceivedText);
    }

    private sealed class StubSendUiService : ISendUiService
    {
        public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SendListItem>>(new List<SendListItem>
            {
                new("s1", "分享", SendType.Text, "截止", "https://vault.example/#/send/acc/seed"),
            });
        public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, Core.Models.SendFileSource? file, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, Core.Models.SendFileSource? file, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteSendAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public string? CopyShareLink(SendListItem item) => item.Link;
        public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<SendEditorDraft?>(null);
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
