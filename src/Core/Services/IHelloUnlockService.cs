namespace Core.Services;

// Windows Hello 解锁管理:设/清/查 + 读挑战。SetHello 要求 vault 解锁。
public interface IHelloUnlockService
{
    bool IsHelloSet { get; }
    // signature = Hello 对 challenge 的签名;challenge = 启用时生成的随机挑战。
    void SetHello(byte[] signature, byte[] challenge);
    void ClearHello();
    // 从持久化会话读回挑战(base64 解码);未启用返回 null。供解锁流重新签名。
    byte[]? LoadChallenge();
}
