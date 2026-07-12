using Core.Session;
using Vault;

namespace Vault.Tests;

// ISendCompensationClient 的手写测试替身。记录每次补偿删除的 sendId、所用【冻结 binding】、以及所用令牌
// 是否已取消 —— 用于断言补偿删除:(1)走的是独立的补偿客户端而非 lease 绑定的普通 ISendApiClient;
// (2)携带操作开始时捕获的冻结 binding;(3)用【未取消】的独立令牌(即便调用方令牌已取消也能完成清理)。
// 可按 sendId 注入清理失败,验证 SendUploadCompensationException 路径。
public sealed class FakeSendCompensationClient : ISendCompensationClient
{
    public List<string> DeletedSendIds { get; } = [];
    public AccessTokenBinding? LastBinding { get; private set; }
    public bool LastTokenWasCanceled { get; private set; }
    public Dictionary<string, Exception> ThrowFor { get; } = [];

    public Task DeleteAsync(AccessTokenBinding binding, string sendId, CancellationToken ct = default)
    {
        LastBinding = binding;
        LastTokenWasCanceled = ct.IsCancellationRequested;
        DeletedSendIds.Add(sendId);
        return ThrowFor.TryGetValue(sendId, out var failure)
            ? Task.FromException(failure)
            : Task.CompletedTask;
    }
}
