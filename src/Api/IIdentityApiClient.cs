using Api.Dtos;

namespace Api;

public interface IIdentityApiClient : IAnonymousApiRole
{
    Task<ConfigResponse> GetConfigAsync(CancellationToken ct = default);
    Task<PreloginResponse> PreloginAsync(string email, CancellationToken ct = default);
    Task<ConnectTokenResult> ConnectTokenAsync(
        ConnectTokenRequest request,
        CancellationToken ct = default);
    Task SendTwoFactorEmailLoginAsync(
        string email,
        string masterPasswordHash,
        string deviceIdentifier,
        CancellationToken ct = default);
}
