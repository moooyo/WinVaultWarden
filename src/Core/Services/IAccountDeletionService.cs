using Core.Abstractions;
using Core.Models;

namespace Core.Services;

public interface IAccountDeletionService
{
    AccountDeletionPreflight GetPreflight();

    Task<AccountDeletionOtpRequestResult> RequestOtpAsync(
        CancellationToken ct = default);

    Task<AccountDeletionResult> DeleteAsync(
        AccountDeletionCredential credential,
        CancellationToken ct = default);

    AccountDeletionTombstoneReadResult ReadPending(
        AccountDeletionBinding binding);

    AccountDeletionTombstoneReadAllResult ReadPending();

    Task<AccountDeletionResult> ResumeConfirmedCleanupAsync(
        AccountDeletionTombstoneRecord expected,
        CancellationToken ct = default);

    Task<AccountDeletionLocalResolutionResult> ClearLocalDataForPendingAsync(
        AccountDeletionTombstoneRecord expected,
        CancellationToken ct = default);

}
