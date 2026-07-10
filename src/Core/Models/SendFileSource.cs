namespace Core.Models;

// 可重开的明文文件源:主上传路径唯一接受的输入形态。OpenReadAsync 每次返回一个从头开始的新流,
// 使 Send 文件体的两遍式流加密(第一遍算 MAC、第二遍写密文)得以在常量内存下复用同一内容,
// 绝不把整份明文/密文缓存进内存。Length 是选择文件时记录的明文字节数,任何 API 调用前先据此校验上限。
public sealed record SendFileSource(
    string FileName,
    long Length,
    Func<CancellationToken, ValueTask<Stream>> OpenReadAsync)
{
    // Bitwarden 客户端固定的明文上限:500 MiB。服务端另有 525 MB 的加密余量
    // (vaultwarden src/api/core/sends.rs:41-42, SIZE_525_MB=550_502_400),但客户端以明文口径拦截。
    public const long MaxPlaintextBytes = 500L * 1024 * 1024;

    public static void ValidateLength(long length)
    {
        if (length < 0 || length > MaxPlaintextBytes)
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                $"Send file must be between 0 and {MaxPlaintextBytes} bytes.");
    }
}
