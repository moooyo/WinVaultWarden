using Api.Dtos;

namespace Api;

public interface IRegistrationApiClient : IAnonymousApiRole
{
    Task RegisterAsync(RegisterRequest request, CancellationToken ct = default);
}
