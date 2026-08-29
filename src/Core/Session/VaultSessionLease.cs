using Core.Models;

namespace Core.Session;

/// <summary>
/// 不可伪造的会话租约:仅 <see cref="Vault.VaultSession"/> 能构造(内部构造函数 + InternalsVisibleTo)。
/// Generation/Stamp 是唯一真源,租约只是其时间点快照,用于跨调用边界证明"这次操作仍针对当前会话"。
/// </summary>
public sealed class VaultSessionLease
{
    internal VaultSessionLease(
        long generation,
        string accountId,
        ServerEndpoints endpoints,
        CancellationToken cancellationToken,
        Guid stamp)
    {
        Generation = generation;
        AccountId = accountId;
        Endpoints = endpoints;
        CancellationToken = cancellationToken;
        Stamp = stamp;
    }

    public long Generation { get; }
    public string AccountId { get; }
    public ServerEndpoints Endpoints { get; }
    public ServerOrigin Origin => Endpoints.WebVault.Origin;
    public CancellationToken CancellationToken { get; }
    internal Guid Stamp { get; }

    public bool SameSessionAs(VaultSessionLease? other) =>
        other is not null
        && Stamp == other.Stamp
        && Generation == other.Generation
        && AccountId == other.AccountId
        && Endpoints == other.Endpoints;
}
