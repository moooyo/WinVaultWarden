using Core.Session;

namespace Core.Services;

// 用 refresh_token 刷新 access_token。成功更新会话并持久化轮换后的 token 返回 true;
// 失败返回 false(并锁定会话)。并发安全:同时多次调用只发一次刷新。
// 刷新绑定显式租约:仅当租约仍对应当前 account/origin/generation 时才提交轮换后的 token。
public interface ITokenRefresher
{
    Task<bool> TryRefreshAsync(
        VaultSessionLease lease,
        CancellationToken ct = default);

    Task<bool> TryRefreshAsync(
        VaultSessionLease lease,
        AccessTokenBinding failedAuthorization,
        CancellationToken ct = default) =>
        TryRefreshAsync(lease, ct);
}
