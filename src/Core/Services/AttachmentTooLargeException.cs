namespace Core.Services;

// 待上传明文超过客户端上限时抛出。Message 含实际字节数与上限字节数。
public sealed class AttachmentTooLargeException : Exception
{
    public long ActualBytes { get; }
    public long MaxBytes { get; }

    public AttachmentTooLargeException(long actual, long max)
        : base($"附件明文 {actual} 字节超过上限 {max} 字节。")
    {
        ActualBytes = actual;
        MaxBytes = max;
    }
}
