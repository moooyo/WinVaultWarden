using App.ViewModels;
using App.Services;
using Core.Models;
using Core.Services;
using Core.Session;
using Vault;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public sealed class LoginViewModelApiKeyTests : AppPreferencesTestBase
{
    [Fact]
    public async Task ExplicitAccountSwitchRevokesMetadataButFormInitializationDoesNot()
    {
        var revoked = 0;
        var model = new LoginViewModel(new RecordingAuthService(), null,
            beforeAccountChange: () => revoked++);
        model.PrepareAccount("https://vault.example", "me@example.com");
        Assert.Equal(0, revoked);
        await model.SwitchAccountCommand.ExecuteAsync(null);
        Assert.Equal(1, revoked);
    }

    [Fact]
    public void SavedAccountSelectionRevokesMetadataBeforeChangingTheSavedBinding()
    {
        var revoked = false;
        var accounts = new ObservingSavedAccounts(() => Assert.True(revoked));
        var model = new LoginViewModel(new RecordingAuthService(), null, savedAccountService: accounts,
            beforeAccountChange: () => revoked = true);

        model.SelectSavedAccountCommand.Execute(new SavedAccountOption(
            "other-account", "other@example.com", ServerEndpoints.ForSelfHosted("https://vault.example"), false));

        Assert.True(revoked);
        Assert.Equal(1, accounts.Activations);
    }

    [Fact]
    public void BusySavedAccountSelectionDoesNotRevokeOrSwitchTheActiveBinding()
    {
        var revoked = false;
        var accounts = new ObservingSavedAccounts(() => { });
        var model = new LoginViewModel(new RecordingAuthService(), null, savedAccountService: accounts,
            beforeAccountChange: () => revoked = true) { IsBusy = true };

        model.SelectSavedAccountCommand.Execute(new SavedAccountOption(
            "other-account", "other@example.com", ServerEndpoints.ForSelfHosted("https://vault.example"), false));

        Assert.False(revoked);
        Assert.Equal(0, accounts.Activations);
    }

    private sealed class ObservingSavedAccounts(Action beforeActivation) : ISavedAccountService
    {
        public int Activations { get; private set; }
        public IReadOnlyList<SavedAccountOption> GetAccounts() => [];
        public bool TryActivate(SavedAccountOption account)
        {
            beforeActivation();
            Activations++;
            return false;
        }
    }

    private const string ClientId = "user.aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string ApiSecret = "synthetic-api-secret";
    private const string Password = "local master password";

    [Fact]
    public void ApiKeyEntryUsesTheExistingAccountValidationBeforeOpeningTheForm()
    {
        var auth = new RecordingAuthService();
        var viewModel = CreateViewModel(auth);
        viewModel.Email = string.Empty;

        viewModel.OpenApiKeySignInCommand.Execute(null);

        Assert.True(viewModel.IsAccountStage);
        Assert.False(viewModel.IsApiKeyMode);
        Assert.Null(auth.ApiKeyCall);
        Assert.Equal(LoginFocusTarget.Email, viewModel.FocusTarget);
    }

    [Fact]
    public void ApiKeyFormHasItsOwnFocusAndSuppressesIncompatiblePasswordLoginActions()
    {
        var viewModel = CreateViewModel(new RecordingAuthService());

        viewModel.OpenApiKeySignInCommand.Execute(null);

        Assert.True(viewModel.IsPasswordStage);
        Assert.True(viewModel.IsApiKeyMode);
        Assert.Equal(LoginFocusTarget.ApiKeyClientId, viewModel.FocusTarget);
        Assert.False(viewModel.CanRequestPasswordHint);
        Assert.False(viewModel.ShowLoginWithDeviceOption);
        Assert.False(viewModel.CanUseApiKeySignIn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectExecutionOfADisabledApiKeyCommandDoesNotChangeTheActiveAuthenticationForm(bool busy)
    {
        var viewModel = CreateViewModel(new RecordingAuthService());
        if (busy)
            viewModel.IsBusy = true;
        else
            viewModel.PrepareUnlock("https://vault.example", "me@example.com");
        viewModel.MasterPassword = Password;
        var stage = viewModel.Stage;
        Assert.False(viewModel.OpenApiKeySignInCommand.CanExecute(null));

        viewModel.OpenApiKeySignInCommand.Execute(null);

        Assert.Equal(stage, viewModel.Stage);
        Assert.False(viewModel.IsApiKeyMode);
        Assert.Equal(Password, viewModel.MasterPassword);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackAndSensitiveCleanupRemoveApiCredentials(bool back)
    {
        var viewModel = CreateReadyViewModel(new RecordingAuthService());

        if (back)
            viewModel.BackCommand.Execute(null);
        else
            viewModel.ClearSensitive();

        Assert.Empty(viewModel.ApiKeySecret);
        Assert.Empty(viewModel.ApiKeyClientId);
        Assert.Empty(viewModel.MasterPassword);
        Assert.False(viewModel.IsApiKeyMode);
        if (back)
            Assert.True(viewModel.IsAccountStage);
    }

    [Fact]
    public void DeactivationDoesNotKeepApiSecretsInADetachedLoginPage()
    {
        var viewModel = CreateReadyViewModel(new RecordingAuthService());

        viewModel.Deactivate();

        Assert.Empty(viewModel.ApiKeySecret);
    }

    [Fact]
    public async Task LoginRoutesToTheApiKeyServiceAndClearsSecretOnSuccess()
    {
        var auth = new RecordingAuthService
        {
            ApiResult = new AuthResult.Success(TestSessionLeases.CreateSessionLease()),
        };
        var viewModel = CreateReadyViewModel(auth);
        VaultSessionLease? accepted = null;
        viewModel.SetSuccessCallback(lease => accepted = lease);

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.Equal(
            ("https://vault.example", "me@example.com", ClientId, ApiSecret, Password),
            auth.ApiKeyCall);
        Assert.Equal(0, auth.PasswordLoginCalls);
        Assert.NotNull(accepted);
        Assert.Empty(viewModel.ApiKeySecret);
        Assert.Empty(viewModel.MasterPassword);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task LoginFailureRequiresReentryOfTheSecretForAnExplicitRetry()
    {
        var auth = new RecordingAuthService();
        var viewModel = CreateReadyViewModel(auth);

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.NotNull(auth.ApiKeyCall);
        Assert.Empty(viewModel.ApiKeySecret);
        Assert.True(viewModel.IsApiKeyMode);
        Assert.True(viewModel.HasStatus);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(0, auth.PasswordLoginCalls);

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.Equal(1, auth.ApiKeyCalls);
        Assert.Equal(LoginFocusTarget.ApiKeySecret, viewModel.FocusTarget);
    }

    private static LoginViewModel CreateViewModel(RecordingAuthService auth) => new(auth)
    {
        SelectedServerOptionIndex = 2,
        ServerUrl = "https://vault.example",
        Email = "me@example.com",
    };

    private static LoginViewModel CreateReadyViewModel(RecordingAuthService auth)
    {
        var viewModel = CreateViewModel(auth);
        viewModel.OpenApiKeySignInCommand.Execute(null);
        viewModel.ApiKeyClientId = ClientId;
        viewModel.ApiKeySecret = ApiSecret;
        viewModel.MasterPassword = Password;
        return viewModel;
    }

    private sealed class RecordingAuthService : IAuthService
    {
        private readonly VaultSession _attempts = new();
        public (string ServerUrl, string Email, string ClientId, string ClientSecret, string Password)? ApiKeyCall { get; private set; }
        public int ApiKeyCalls { get; private set; }
        public int PasswordLoginCalls { get; private set; }
        public AuthResult ApiResult { get; init; } = new AuthResult.Failure(
            "The supplied API credentials were rejected.", AuthFailureKind.InvalidCredentials);

        public AuthenticationAttemptLease BeginAuthenticationAttempt() => _attempts.BeginAuthenticationAttempt();
        public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt) => _attempts.AbandonAuthenticationAttempt(attempt);
        public Task<AuthResult> LoginWithApiKeyAsync(
            AuthenticationAttemptLease attempt,
            string serverUrl,
            string email,
            string clientId,
            string clientSecret,
            string masterPassword,
            CancellationToken ct = default)
        {
            ApiKeyCall = (serverUrl, email, clientId, clientSecret, masterPassword);
            ApiKeyCalls++;
            return Task.FromResult(ApiResult);
        }

        public Task<AuthResult> LoginAsync(AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default)
        {
            PasswordLoginCalls++;
            return Task.FromResult<AuthResult>(new AuthResult.Failure("Password login was not expected."));
        }

        public Task<AuthResult> SubmitTwoFactorAsync(AuthenticationAttemptLease attempt, string code, int provider, CancellationToken ct = default, bool rememberDevice = true) => Unexpected();
        public Task<bool> ResendTwoFactorEmailAsync(AuthenticationAttemptLease attempt, CancellationToken ct = default) => Task.FromResult(false);
        public Task<AuthResult> UnlockAsync(AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default) => Unexpected();
        public Task<AuthResult> UnlockWithPinAsync(AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default) => Unexpected();
        public Task<AuthResult> UnlockWithHelloAsync(AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default) => Unexpected();
        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LockAsync(VaultSessionLease expectedSession, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> LogoutAsync(VaultSessionLease expectedSession, CancellationToken ct = default) => Task.FromResult(true);
        private static Task<AuthResult> Unexpected() => Task.FromResult<AuthResult>(new AuthResult.Failure("An unexpected authentication path was invoked."));
    }
}
