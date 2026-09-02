using Api.Dtos;

namespace Api;

public interface IServerConfigApiClient : IAnonymousApiRole, IAuthenticatedApiRole
{
    Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default);
}
