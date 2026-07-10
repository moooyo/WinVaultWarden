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
        ServerOrigin origin,
        CancellationToken cancellationToken,
        Guid stamp)
    {
        Generation = generation;
        AccountId = accountId;
        Origin = origin;
        CancellationToken = cancellationToken;
        Stamp = stamp;
    }

    public long Generation { get; }
    public string AccountId { get; }
    public ServerOrigin Origin { get; }
    public CancellationToken CancellationToken { get; }
    internal Guid Stamp { get; }
}
