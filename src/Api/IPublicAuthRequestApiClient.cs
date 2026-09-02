using Api.Dtos;

namespace Api;

public interface IPublicAuthRequestApiClient : IAnonymousApiRole
{
    Task<bool> GetKnownDeviceAsync(
        string email,
        string deviceIdentifier,
        CancellationToken ct = default) =>
        Task.FromException<bool>(
            new NotSupportedException("Known-device lookup is unavailable."));

    Task<AuthRequestResponse> CreateAsync(
        AuthRequestRequest request, CancellationToken ct = default);
    Task<AuthRequestResponse> GetResponseAsync(
        string id, string code, CancellationToken ct = default);
}
