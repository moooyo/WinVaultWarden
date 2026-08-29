using Core.Session;

namespace Vault;

/// <summary>
/// Narrow client that deletes a newly created Send with the authorization binding recorded by the
/// successful metadata-create request. It remains usable after the originating lease is invalidated.
/// </summary>
public interface ISendCompensationClient
{
    /// <summary>Deletes <paramref name="sendId"/> using an origin-bound authorization receipt.</summary>
    Task DeleteAsync(AccessTokenBinding binding, string sendId, CancellationToken ct = default);
}
