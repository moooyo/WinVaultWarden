using Core.Session;

namespace Core.Services;

public enum AccountSecurityChangeOutcome
{
    Confirmed,
    Uncertain,
}

/// <summary>
/// A security-changing request may commit even if its response is lost. The receipt always
/// identifies the original account and never authorizes clearing a subsequently activated account.
/// </summary>
public sealed record AccountSecurityChangeReceipt(
    AccountSecurityChangeOutcome Outcome,
    VaultSessionLease OriginalSession,
    VaultSessionLease? InvalidatedSession,
    bool LocalCleanupSucceeded);

public sealed record AccountManagementAvailability(bool CanChangeEmail, string? EmailChangeUnavailableReason);

public interface IAccountManagementService
{
    AccountManagementAvailability GetAvailability();
    Task RequestEmailChangeAsync(string currentPassword, string newEmail, CancellationToken ct = default);
    Task<AccountSecurityChangeReceipt> ChangeEmailAsync(
        string currentPassword, string newEmail, string token, CancellationToken ct = default);
    Task RequestEmailVerificationAsync(CancellationToken ct = default);
    Task VerifyEmailAsync(string token, CancellationToken ct = default);
    Task<AccountSecurityChangeReceipt> RevokeAllSessionsAsync(
        string currentPassword, CancellationToken ct = default);
}
