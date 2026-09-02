using Api.Dtos;

namespace Api;

// Authentication-request endpoints used by the approving, authenticated device.
public interface IAuthRequestApiClient : IAuthenticatedApiRole
{
    Task<AuthRequestResponse> CreateAdminAsync(
        AuthRequestRequest request,
        CancellationToken ct = default) =>
        Task.FromException<AuthRequestResponse>(
            new NotSupportedException("Administrator auth-request creation is unavailable."));

    Task<AuthRequestResponse> GetAsync(
        string id,
        CancellationToken ct = default) =>
        Task.FromException<AuthRequestResponse>(
            new NotSupportedException("Authenticated auth-request lookup is unavailable."));

    // The approving device fetches all pending requests.
    Task<AuthRequestListResponse> GetPendingAsync(CancellationToken ct = default);

    // The approving device approves or rejects a request.
    Task<AuthRequestResponse> ApproveAsync(string id, AuthResponseRequest request, CancellationToken ct = default);
}
