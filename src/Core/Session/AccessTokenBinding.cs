using Core.Models;

namespace Core.Session;

/// <summary>
/// 从 <see cref="VaultSessionLease"/> 派生的 access token 绑定快照,用于把外发请求的令牌
/// 与账户/服务端来源/generation 三元组绑定,防止跨账户/跨服务端/跨代际误用令牌。
/// </summary>
public sealed record AccessTokenBinding(
    string AccountId,
    ServerOrigin Origin,
    long Generation,
    string Token);
