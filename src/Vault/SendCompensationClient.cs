using Api;
using Core.Session;

namespace Vault;

/// <summary>
/// <see cref="ISendCompensationClient"/> 的生产实现:用冻结 <see cref="AccessTokenBinding"/> 造一枚
/// 【一次性】认证客户端,只发一个 DELETE,用后即弃(dispose)。
/// </summary>
/// <remarks>
/// 绝不复用会话/lease 绑定的 <c>IVaultApiClientProvider</c> —— 那个 provider 的 token provider 在
/// lease 失效后返回 null,补偿 DELETE 无 Bearer 必被 401 拒绝、留下孤儿 Send。这里的 token provider 恒
/// 返回冻结 binding;refresh 恒 <c>false</c>(补偿绝不触发刷新——令牌是单次冻结值,401 即失败,不得静默续期)。
/// 客户端绑定 <c>binding.Origin</c>,<c>AuthHeaderHandler</c> 只在目标 origin 与 binding 一致时附上 Bearer,
/// 故令牌绝不跨 origin 泄漏。
/// </remarks>
public sealed class SendCompensationClient : ISendCompensationClient
{
    private readonly IApiClientFactory _factory;

    public SendCompensationClient(IApiClientFactory factory) => _factory = factory;

    public async Task DeleteAsync(AccessTokenBinding binding, string sendId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(sendId);

        using var client = _factory.CreateAuthenticated(
            binding.Origin,
            () => binding,
            _ => Task.FromResult(false));
        await client.DeleteSendAsync(sendId, ct);
    }
}
