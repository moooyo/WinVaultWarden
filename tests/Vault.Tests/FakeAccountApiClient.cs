using Api;
using Api.Dtos;
using System.Net;

namespace Vault.Tests;

public sealed class FakeAccountApiClient : IAccountApiClient, IRegistrationApiClient
{
    public ProfileUpdateRequest? Profile;
    public ChangePasswordRequest? Password;
    public UpdateTemporaryPasswordRequest? TemporaryPassword;
    public ChangeKdfRequest? Kdf;
    public DeleteAccountRequest? AccountDeletion;
    public int AccountDeletionOtpRequestCount;
    public int AccountDeletionRequestCount;
    public Func<CancellationToken, Task>? RequestAccountDeletionOtp;
    public Func<DeleteAccountRequest, CancellationToken, Task>? DeleteAccount;
    public Exception? Throw;

    public Task UpdateProfileAsync(ProfileUpdateRequest r, CancellationToken ct = default)
    {
        if (Throw != null) throw Throw;
        Profile = r;
        return Task.CompletedTask;
    }

    public Task ChangePasswordAsync(ChangePasswordRequest r, CancellationToken ct = default)
    {
        if (Throw != null) throw Throw;
        Password = r;
        return Task.CompletedTask;
    }

    public Task UpdateTemporaryPasswordAsync(
        UpdateTemporaryPasswordRequest r,
        CancellationToken ct = default)
    {
        if (Throw != null) throw Throw;
        TemporaryPassword = r;
        return Task.CompletedTask;
    }

    public Task ChangeKdfAsync(ChangeKdfRequest r, CancellationToken ct = default)
    {
        if (Throw != null) throw Throw;
        Kdf = r;
        return Task.CompletedTask;
    }

    public Task RequestAccountDeletionOtpAsync(CancellationToken ct = default)
    {
        AccountDeletionOtpRequestCount++;
        if (Throw is not null)
            throw Throw;
        return RequestAccountDeletionOtp?.Invoke(ct) ?? Task.CompletedTask;
    }

    public Task DeleteAccountAsync(
        DeleteAccountRequest request,
        CancellationToken ct = default)
    {
        AccountDeletionRequestCount++;
        AccountDeletion = request;
        if (Throw is not null)
            throw Throw;
        return DeleteAccount?.Invoke(request, ct) ?? Task.CompletedTask;
    }

    public RegisterRequest? Register;
    public RegisterVerificationRequest? RegisterVerification;
    public RegisterFinishRequest? RegisterFinish;
    public bool SupportsRegistrationFinish;
    public RegistrationVerificationResult RegistrationVerificationResult =
        new RegistrationVerificationResult.InlineToken("registration-token");
    public Exception? RegistrationVerificationThrow;
    public Exception? RegistrationFinishThrow;
    public Exception? LegacyRegistrationThrow;
    public RegistrationWriteResult RegistrationFinishResult =
        new RegistrationWriteResult.Completed();
    public RegistrationWriteResult LegacyRegistrationResult =
        new RegistrationWriteResult.Completed();
    public int RegistrationVerificationRequestCount;
    public int RegistrationFinishRequestCount;
    public int LegacyRegistrationRequestCount;

    public Task<RegistrationVerificationResult> RequestRegistrationVerificationTokenAsync(
        RegisterVerificationRequest request,
        CancellationToken ct = default)
    {
        RegistrationVerificationRequestCount++;
        var error = RegistrationVerificationThrow ?? Throw;
        if (error is not null)
            throw error;
        RegisterVerification = request;
        if (!SupportsRegistrationFinish)
        {
            throw new VaultWriteException(
                HttpStatusCode.NotFound,
                "Not Found",
                null);
        }
        return Task.FromResult(RegistrationVerificationResult);
    }

    public Task<RegistrationWriteResult> FinishRegistrationAsync(
        RegisterFinishRequest request,
        CancellationToken ct = default)
    {
        RegistrationFinishRequestCount++;
        var error = RegistrationFinishThrow ?? Throw;
        if (error is not null)
            throw error;
        RegisterFinish = request;
        return Task.FromResult(RegistrationFinishResult);
    }

    public Task<RegistrationWriteResult> RegisterAsync(
        RegisterRequest r,
        CancellationToken ct = default)
    {
        LegacyRegistrationRequestCount++;
        var error = LegacyRegistrationThrow ?? Throw;
        if (error is not null)
            throw error;
        Register = r;
        return Task.FromResult(LegacyRegistrationResult);
    }
}
