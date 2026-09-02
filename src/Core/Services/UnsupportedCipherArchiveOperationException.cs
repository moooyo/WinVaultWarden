namespace Core.Services;

public sealed class UnsupportedCipherArchiveOperationException : NotSupportedException
{
    public UnsupportedCipherArchiveOperationException(bool archive)
        : base(archive
            ? "当前服务器或账户未确认支持归档条目。为避免不兼容写入，本次操作已被阻止。"
            : "当前服务器未确认支持取消归档。为避免不兼容写入，本次操作已被阻止。")
    {
        Archive = archive;
    }

    public bool Archive { get; }
}
