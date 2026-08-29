using Core.Models;

namespace Api;

public interface IOriginBoundApiClient
{
    ServerEndpoints Endpoints { get; }
    ServerOrigin Origin { get; }
}
