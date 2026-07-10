using Api.Dtos;

namespace Api;

public interface IPublicAuthRequestApiClient : IAnonymousApiRole
{
    Task<AuthRequestResponse> CreateAsync(
        AuthRequestRequest request, CancellationToken ct = default);
    Task<AuthRequestResponse> GetResponseAsync(
        string id, string code, CancellationToken ct = default);
}
