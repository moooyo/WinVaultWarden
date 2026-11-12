using App.ViewModels;
using Core.Services;
using Core.Session;
using Vault;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public sealed class LoginViewModelPinRestartPolicyTests : AppPreferencesTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrepareUnlockOffersPinOnlyAfterTheRestartRequirementIsSatisfied(
        bool requiresMasterPassword)
    {
        var viewModel = new LoginViewModel(
            new UnusedAuthService(),
            demoVault: null,
            pin: new RestartPolicyPinService(requiresMasterPassword));

        viewModel.PrepareUnlock("https://vault.example", "me@example.com");

        Assert.Equal(
            requiresMasterPassword ? LoginStage.Unlock : LoginStage.PinUnlock,
            viewModel.Stage);
        Assert.Equal(!requiresMasterPassword, viewModel.IsPinUnlockAvailable);
        Assert.Equal(!requiresMasterPassword, viewModel.IsPinUnlockStage);
    }

    private sealed class RestartPolicyPinService(bool requiresMasterPassword) : IPinService
    {
        public bool IsPinSet => true;
        public bool RequireMasterPasswordOnRestart => true;
        public bool RequiresMasterPasswordBeforePin => requiresMasterPassword;
        public void SetPin(string pin) => throw new InvalidOperationException("Unexpected PIN enrollment.");
        public void ClearPin() => throw new InvalidOperationException("Unexpected PIN removal.");
    }

    private sealed class UnusedAuthService : IAuthService
    {
        private readonly VaultSession _attempts = new();

        public AuthenticationAttemptLease BeginAuthenticationAttempt() =>
            _attempts.BeginAuthenticationAttempt();
        public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt) =>
            _attempts.AbandonAuthenticationAttempt(attempt);
        public Task<AuthResult> LoginAsync(
            AuthenticationAttemptLease attempt,
            string serverUrl,
            string email,
            string masterPassword,
            CancellationToken ct = default) => throw Unexpected();
        public Task<AuthResult> SubmitTwoFactorAsync(
            AuthenticationAttemptLease attempt,
            string code,
            int provider,
            CancellationToken ct = default,
            bool rememberDevice = true) => throw Unexpected();
        public Task<bool> ResendTwoFactorEmailAsync(
            AuthenticationAttemptLease attempt,
            CancellationToken ct = default) => throw Unexpected();
        public Task<AuthResult> UnlockAsync(
            AuthenticationAttemptLease attempt,
            string masterPassword,
            CancellationToken ct = default) => throw Unexpected();
        public Task<AuthResult> UnlockWithPinAsync(
            AuthenticationAttemptLease attempt,
            string pin,
            CancellationToken ct = default) => throw Unexpected();
        public Task<AuthResult> UnlockWithHelloAsync(
            AuthenticationAttemptLease attempt,
            byte[] signature,
            CancellationToken ct = default) => throw Unexpected();
        public Task LockAsync(CancellationToken ct = default) => throw Unexpected();
        public Task LockAsync(
            VaultSessionLease expectedSession,
            CancellationToken ct = default) => throw Unexpected();
        public Task LogoutAsync(CancellationToken ct = default) => throw Unexpected();
        public Task<bool> LogoutAsync(
            VaultSessionLease expectedSession,
            CancellationToken ct = default) => throw Unexpected();

        private static InvalidOperationException Unexpected() =>
            new("Preparing the unlock page must not start authentication.");
    }
}
