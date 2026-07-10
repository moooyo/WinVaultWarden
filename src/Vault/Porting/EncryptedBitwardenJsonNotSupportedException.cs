namespace Vault.Porting;

// 客户端不支持解析加密的 Bitwarden JSON 导出（{"encrypted":true,...}）。
// 必须在构造任何 Folder/Cipher 领域对象、调用写入 API 或触发 sync 之前抛出，
// 避免把加密块当作明文误解析或误当作可重新加密的数据处理。
public sealed class EncryptedBitwardenJsonNotSupportedException : NotSupportedException
{
    public EncryptedBitwardenJsonNotSupportedException()
        : base("不支持加密的 Bitwarden JSON；请先导出未加密 JSON 后重试。")
    {
    }
}
