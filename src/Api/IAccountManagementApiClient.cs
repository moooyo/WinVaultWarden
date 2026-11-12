using Api.Dtos;

namespace Api;

/// <summary>Account email and all-session revocation endpoints.</summary>
public interface IAccountManagementApiClient : IAuthenticatedApiRole
{
    Task RequestEmailChangeAsync(AccountEmailTokenRequest request, CancellationToken ct = default);
    Task ChangeEmailAsync(AccountEmailChangeRequest request, CancellationToken ct = default);
    Task RequestEmailVerificationAsync(CancellationToken ct = default);
    Task VerifyEmailTokenAsync(AccountEmailVerificationRequest request, CancellationToken ct = default);
    Task RevokeAllSessionsAsync(AccountSecurityStampRequest request, CancellationToken ct = default);
}
