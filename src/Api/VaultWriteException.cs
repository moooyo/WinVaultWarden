namespace Api;

// 写操作被服务端拒绝(4xx)时抛出,Message 为服务端返回的可读错误。
public sealed class VaultWriteException : Exception
{
    public VaultWriteException(string message) : base(message)
    {
    }
}
