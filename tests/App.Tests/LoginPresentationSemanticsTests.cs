using Api;
using App.ViewModels;
using Core.Services;
using Core.Session;
using Vault;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public sealed class LoginPresentationSemanticsTests : AppPreferencesTestBase
{
    private const string ApiClientId = "user.aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    [Theory]
    [InlineData(true, LoginFocusTarget.ServerUrl)]
    [InlineData(false, LoginFocusTarget.Email)]
    public async Task AccountValidationExplicitlyIdentifiesTheInvalidField(bool invalidServer, LoginFocusTarget expected)
    {
        var viewModel = CreateViewModel(new RecordingAuth());
        if (invalidServer)
            viewModel.ServerUrl = "not-a-url";
        else
            viewModel.Email = string.Empty;

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.Equal(expected, viewModel.InlineErrorTarget);
        Assert.Equal(expected, viewModel.FocusTarget);
        Assert.Equal(LoginStatusKind.Error, viewModel.StatusKind);
        Assert.False(viewModel.IsUnlockPrompt);
    }

    [Fact]
    public async Task MissingMasterPasswordUsesAFieldErrorWithoutInspectingTheDisplayedWords()
    {
        var viewModel = await CreatePasswordViewModel(new RecordingAuth());

        await viewModel.LoginCommand.ExecuteAsync(null);
        viewModel.Status = "Geben Sie das Master-Passwort ein.";

        Assert.Equal(LoginFocusTarget.MasterPassword, viewModel.InlineErrorTarget);
        Assert.Equal(LoginFocusTarget.MasterPassword, viewModel.FocusTarget);
        Assert.Equal(LoginStatusKind.Error, viewModel.StatusKind);
    }

    [Theory]
    [InlineData(true, LoginFocusTarget.ApiKeyClientId)]
    [InlineData(false, LoginFocusTarget.ApiKeySecret)]
    public async Task ApiKeyValidationIdentifiesTheCorrectInput(bool missingClientId, LoginFocusTarget expected)
    {
        var auth = new RecordingAuth();
        var viewModel = CreateViewModel(auth);
        viewModel.OpenApiKeySignInCommand.Execute(null);
        viewModel.ApiKeyClientId = missingClientId ? string.Empty : ApiClientId;
        viewModel.ApiKeySecret = missingClientId ? "synthetic secret" : string.Empty;
        viewModel.MasterPassword = "synthetic master password";

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.Equal(expected, viewModel.InlineErrorTarget);
        Assert.Equal(expected, viewModel.FocusTarget);
        Assert.Equal(0, auth.ApiKeyCalls);
    }

    [Fact]
    public async Task MissingPinReplacesTheUnlockPromptWithAnExplicitFieldError()
    {
        var viewModel = new LoginViewModel(new RecordingAuth(), demoVault: null, pin: new PresentPin());
        viewModel.PrepareUnlock("https://vault.example", "me@example.com");
        Assert.True(viewModel.IsUnlockPrompt);
        viewModel.Status = "PIN を入力してロックを解除してください。";
        Assert.True(viewModel.IsUnlockPrompt);

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginFocusTarget.Pin, viewModel.InlineErrorTarget);
        Assert.False(viewModel.IsUnlockPrompt);
    }

    [Theory]
    [InlineData(AuthFailureKind.Network)]
    [InlineData(AuthFailureKind.Timeout)]
    public async Task TransportFailureRemainsGlobalAfterAnEarlierFieldValidationError(AuthFailureKind kind)
    {
        var auth = new RecordingAuth { LoginResult = new AuthResult.Failure("A transport failure occurred.", kind) };
        var viewModel = await CreatePasswordViewModel(auth);
        await viewModel.LoginCommand.ExecuteAsync(null);
        Assert.Equal(LoginFocusTarget.MasterPassword, viewModel.InlineErrorTarget);
        viewModel.MasterPassword = "synthetic master password";

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginStatusKind.Error, viewModel.StatusKind);
        Assert.Null(viewModel.InlineErrorTarget);
        Assert.Equal(LoginFocusTarget.MasterPassword, viewModel.FocusTarget);
        Assert.False(viewModel.IsUnlockPrompt);
    }

    [Theory]
    [InlineData("Die Anmeldedaten wurden abgelehnt.")]
    [InlineData("入力された認証情報が拒否されました。")]
    public async Task InvalidCredentialsUseTheCurrentSecretFieldRegardlessOfServerLanguage(string message)
    {
        var auth = new RecordingAuth { LoginResult = new AuthResult.Failure(message, AuthFailureKind.InvalidCredentials) };
        var viewModel = await CreatePasswordViewModel(auth);
        viewModel.MasterPassword = "synthetic master password";

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.Equal(LoginFocusTarget.MasterPassword, viewModel.InlineErrorTarget);
        Assert.Equal(LoginFocusTarget.MasterPassword, viewModel.FocusTarget);
    }

    [Fact]
    public async Task InvalidTwoFactorCodeIdentifiesTheCodeField()
    {
        var auth = new RecordingAuth
        {
            LoginResult = new AuthResult.TwoFactorRequired([0]),
            SubmitResult = new AuthResult.Failure("The code was rejected.", AuthFailureKind.InvalidCredentials),
        };
        var viewModel = await CreatePasswordViewModel(auth);
        viewModel.MasterPassword = "synthetic master password";
        await viewModel.LoginCommand.ExecuteAsync(null);
        viewModel.TwoFactorCode = "000000";

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsTwoFactorStage);
        Assert.Equal(LoginFocusTarget.TwoFactorCode, viewModel.InlineErrorTarget);
        Assert.Equal(LoginFocusTarget.TwoFactorCode, viewModel.FocusTarget);
    }

    [Fact]
    public async Task HintCleanupUsesItsPresentationStateAfterTheDisplayLanguageChanges()
    {
        var auth = new RecordingAuth
        {
            HintOperation = _ => Task.FromException(new VaultWriteException("Your password hint is: synthetic hint")),
        };
        var viewModel = await CreatePasswordViewModel(auth);
        await viewModel.RequestPasswordHintCommand.ExecuteAsync(null);
        Assert.Contains("synthetic hint", viewModel.Status);
        viewModel.Status = "Kennworthinweis: synthetic hint";

        viewModel.Deactivate();

        Assert.Empty(viewModel.Status);
        Assert.Null(viewModel.InlineErrorTarget);
    }

    [Fact]
    public async Task OrdinaryFailureStartingWithAHintPhraseIsNotTreatedAsHintData()
    {
        const string ordinaryMessage = "主密码提示：an ordinary authentication error";
        var auth = new RecordingAuth { LoginResult = new AuthResult.Failure(ordinaryMessage, AuthFailureKind.SafeMessage) };
        var viewModel = await CreatePasswordViewModel(auth);
        viewModel.MasterPassword = "synthetic master password";
        await viewModel.LoginCommand.ExecuteAsync(null);
        Assert.Equal(ordinaryMessage, viewModel.Status);

        viewModel.Deactivate();

        Assert.Equal(ordinaryMessage, viewModel.Status);
        Assert.Equal(LoginStatusKind.Error, viewModel.StatusKind);
    }

    [Fact]
    public async Task ReturningToAnAccountStageClearsTheFieldPresentation()
    {
        var viewModel = await CreatePasswordViewModel(new RecordingAuth());
        await viewModel.LoginCommand.ExecuteAsync(null);
        Assert.NotNull(viewModel.InlineErrorTarget);

        viewModel.PrepareAccount("https://another.example", "other@example.com");

        Assert.Null(viewModel.InlineErrorTarget);
        Assert.False(viewModel.IsUnlockPrompt);
        Assert.Empty(viewModel.Status);
    }

    private static LoginViewModel CreateViewModel(RecordingAuth auth) => new(auth)
    {
        SelectedServerOptionIndex = 2,
        ServerUrl = "https://vault.example",
        Email = "me@example.com",
    };

    private static async Task<LoginViewModel> CreatePasswordViewModel(RecordingAuth auth)
    {
        var viewModel = CreateViewModel(auth);
        await viewModel.LoginCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsPasswordStage);
        return viewModel;
    }

    private sealed class PresentPin : IPinService
    {
        public bool IsPinSet => true;
        public void SetPin(string pin) { }
        public void ClearPin() { }
    }

    private sealed class RecordingAuth : IAuthService
    {
        private readonly VaultSession _attempts = new();
        public AuthResult LoginResult { get; init; } = new AuthResult.Failure("Unexpected login.");
        public AuthResult SubmitResult { get; init; } = new AuthResult.Failure("Unexpected two-factor submission.");
        public Func<CancellationToken, Task>? HintOperation { get; init; }
        public int ApiKeyCalls { get; private set; }

        public AuthenticationAttemptLease BeginAuthenticationAttempt() => _attempts.BeginAuthenticationAttempt();
        public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt) => _attempts.AbandonAuthenticationAttempt(attempt);
        public Task RequestPasswordHintAsync(string serverUrl, string email, CancellationToken ct = default) =>
            HintOperation?.Invoke(ct) ?? Task.CompletedTask;
        public Task<AuthResult> LoginAsync(AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default) =>
            Task.FromResult(LoginResult);
        public Task<AuthResult> LoginWithApiKeyAsync(AuthenticationAttemptLease attempt, string serverUrl, string email,
            string clientId, string clientSecret, string masterPassword, CancellationToken ct = default)
        {
            ApiKeyCalls++;
            return Task.FromResult(LoginResult);
        }
        public Task<AuthResult> SubmitTwoFactorAsync(AuthenticationAttemptLease attempt, string code, int provider,
            CancellationToken ct = default, bool rememberDevice = true) => Task.FromResult(SubmitResult);
        public Task<bool> ResendTwoFactorEmailAsync(AuthenticationAttemptLease attempt, CancellationToken ct = default) => Task.FromResult(false);
        public Task<AuthResult> UnlockAsync(AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default) => Task.FromResult(LoginResult);
        public Task<AuthResult> UnlockWithPinAsync(AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default) => Task.FromResult(LoginResult);
        public Task<AuthResult> UnlockWithHelloAsync(AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default) => Task.FromResult(LoginResult);
        public Task LockAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LockAsync(VaultSessionLease expectedSession, CancellationToken ct = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> LogoutAsync(VaultSessionLease expectedSession, CancellationToken ct = default) => Task.FromResult(true);
    }
}
