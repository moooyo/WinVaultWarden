namespace Core.Abstractions;

// Windows Hello 手势 + 签名的抽象(实现在 App 层 WinRT 适配器)。取消/失败/不可用一律返回 null / false,不外抛。
public interface IWindowsHelloAuthenticator
{
    Task<bool> IsAvailableAsync();
    // 启用:创建 Hello 受保护密钥(触发手势)并对 challenge 签名 → 返回签名字节。
    Task<byte[]?> CreateProtectorAsync(byte[] challenge, CancellationToken ct = default);
    // 解锁:打开已有 Hello 密钥并对 challenge 签名(触发手势)→ 返回签名字节。
    Task<byte[]?> GetProtectorAsync(byte[] challenge, CancellationToken ct = default);
}
