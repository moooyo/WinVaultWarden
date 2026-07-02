namespace Core.Services;

// 写操作已在服务端成功,但随后的整库重新同步失败时抛出。
// 语义:变更已持久化到服务端,但本地快照可能过期——调用方应据此提示用户
// "已保存,请手动刷新",而非"写入失败"。InnerException 保留原始同步异常。
public sealed class VaultRefreshException : Exception
{
    public VaultRefreshException(string message, Exception innerException)
        : base(message, innerException) { }
}
