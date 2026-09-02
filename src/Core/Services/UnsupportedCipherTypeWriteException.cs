using Core.Enums;

namespace Core.Services;

public sealed class UnsupportedCipherTypeWriteException : NotSupportedException
{
    public UnsupportedCipherTypeWriteException(CipherType cipherType)
        : base(
            $"当前服务器未确认支持写入 {cipherType}（类型 {(int)cipherType}）。" +
            "为避免数据丢失，本次创建、更新或导入已被阻止；已有条目仍可查看并可用 JSON 完整导出。")
    {
        CipherType = cipherType;
    }

    public CipherType CipherType { get; }
}
