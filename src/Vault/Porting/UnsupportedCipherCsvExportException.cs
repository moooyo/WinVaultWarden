using Core.Enums;

namespace Vault.Porting;

public sealed class UnsupportedCipherCsvExportException : NotSupportedException
{
    public UnsupportedCipherCsvExportException(CipherType cipherType)
        : base(
            $"CSV 无法无损导出 {cipherType}（类型 {(int)cipherType}）条目的专属字段。" +
            "为避免数据丢失，已取消导出；请改用 JSON 格式。")
    {
        CipherType = cipherType;
    }

    public CipherType CipherType { get; }
}
