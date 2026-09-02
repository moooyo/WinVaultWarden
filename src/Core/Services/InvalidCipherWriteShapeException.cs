using Core.Enums;

namespace Core.Services;

public sealed class InvalidCipherWriteShapeException : InvalidOperationException
{
    public InvalidCipherWriteShapeException(CipherType cipherType)
        : base(
            $"条目类型 {cipherType}（{(int)cipherType}）与其内容结构不一致。" +
            "为避免数据丢失，已阻止写入；请重新同步后重试。")
    {
        CipherType = cipherType;
    }

    public CipherType CipherType { get; }
}
