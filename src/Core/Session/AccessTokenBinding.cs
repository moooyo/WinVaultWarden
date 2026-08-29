using Core.Models;

namespace Core.Session;

/// <summary>
/// Captures the account, complete server environment, generation, and token used by one request.
/// The API audience is the exact API endpoint, including its base-path boundary.
/// </summary>
public sealed record AccessTokenBinding(
    string AccountId,
    ServerEndpoints Endpoints,
    long Generation,
    string Token)
{
    public AccessTokenBinding(
        string accountId,
        ServerOrigin origin,
        long generation,
        string token)
        : this(
            accountId,
            ServerEndpoints.ForSelfHosted(origin.Value),
            generation,
            token)
    {
    }

    public ServerEndpoint ApiAudience => Endpoints.Api;
    public ServerOrigin Origin => Endpoints.WebVault.Origin;
}
