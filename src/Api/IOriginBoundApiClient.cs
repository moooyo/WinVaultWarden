using Core.Models;

namespace Api;

public interface IOriginBoundApiClient
{
    ServerOrigin Origin { get; }
}
