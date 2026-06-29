using Core.Models;

namespace Core.Services;

/// <summary>
/// 设备登录授权请求（auth-request）管理服务。
/// 对应 Vaultwarden <c>src/api/core/accounts.rs</c> 中的 auth-requests 端点。
/// </summary>
public interface IAuthRequestService
{
    /// <summary>列出当前账户所有待审批的登录请求。</summary>
    Task<IReadOnlyList<PendingAuthRequest>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>
    /// 批准指定登录请求。
    /// 调用方需先用 <paramref name="publicKey"/> 加密会话密钥后再传入（服务端不做此加密）。
    /// </summary>
    /// <param name="id">auth-request UUID。</param>
    /// <param name="publicKey">发起方公钥（Base64），用于构造加密响应体。</param>
    Task ApproveAsync(string id, string publicKey, CancellationToken ct = default);

    /// <summary>拒绝指定登录请求。</summary>
    /// <param name="id">auth-request UUID。</param>
    Task DenyAsync(string id, CancellationToken ct = default);
}
