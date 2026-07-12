using Core.Session;

namespace Vault;

/// <summary>
/// 孤儿 Send 补偿删除的【窄】接口:唯一职责是用一枚在操作开始、lease 仍 current 时冻结的
/// <see cref="AccessTokenBinding"/> 删除【本次刚创建的新 Send】。
/// </summary>
/// <remarks>
/// 之所以独立于普通业务 API：lease 绑定的 token provider 在 lease 失效(vault Lock)后返回 null,
/// 补偿 DELETE 便无 Bearer,生产环境必被服务端 401 拒绝、留下孤儿记录。冻结 binding 与会话状态解耦,
/// 即便随后 vault 被 Lock 也能带着有效令牌完成 15 秒补偿清理。该接口刻意不暴露任何通用的 stale-token
/// 客户端旁路——它只能删除调用方指定的 sendId,不能用于其它 API。
/// </remarks>
public interface ISendCompensationClient
{
    /// <summary>用冻结 <paramref name="binding"/> 删除 <paramref name="sendId"/>。绑定 binding 的 origin,
    /// 令牌只发原服务端;失败时抛出底层异常,由调用方转成可操作的补偿异常。</summary>
    Task DeleteAsync(AccessTokenBinding binding, string sendId, CancellationToken ct = default);
}
