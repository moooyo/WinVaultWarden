using Api.Dtos;

namespace Api;

public interface ITdeRegistrationApiClient : IAuthenticatedApiRole
{
    Task<OrganizationAutoEnrollStatusResponse> GetAutoEnrollStatusAsync(
        string organizationIdentifier,
        CancellationToken ct = default);

    Task<OrganizationKeysResponse> GetOrganizationKeysAsync(
        string organizationId,
        CancellationToken ct = default);

    Task<PostAccountKeysResponse> PostAccountKeysAsync(
        PostAccountKeysRequest request,
        CancellationToken ct = default);

    Task EnrollResetPasswordAsync(
        string organizationId,
        string userId,
        ResetPasswordEnrollmentRequest request,
        CancellationToken ct = default);
}
