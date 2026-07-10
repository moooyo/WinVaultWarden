namespace Vault;

// 文件型 Send 创建/替换过程中,主操作失败后【补偿删除也失败】时抛出。
// 此时服务端可能残留一条已创建但未完成上传(或已被新件替换语义遗弃)的孤儿 Send 记录。
// 对外只暴露孤儿 SendId 与固定的可操作中文提示;原始错误与清理错误藏进 InnerException 供诊断,
// 绝不在消息中泄露密钥、明文或令牌。
public sealed class SendUploadCompensationException : Exception
{
    public SendUploadCompensationException(
        string orphanedSendId,
        Exception operationError,
        Exception cleanupError)
        : base(
            $"Send 创建未完成且可能需要清理；孤儿 SendId: {orphanedSendId}。",
            new AggregateException(operationError, cleanupError)) =>
        OrphanedSendId = orphanedSendId;

    public string OrphanedSendId { get; }
}
