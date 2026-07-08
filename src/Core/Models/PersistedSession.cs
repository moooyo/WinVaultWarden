using Core.Enums;

namespace Core.Models;

public sealed record PersistedSession(
    string ServerUrl,
    string Email,
    string DeviceIdentifier,
    string RefreshToken,
    string ProtectedUserKey,
    KdfType KdfType,
    int KdfIterations,
    int? KdfMemory,
    int? KdfParallelism)
{
    /// <summary>PIN 派生密钥包住的 UserKey(EncString 文本)。null = 未设 PIN。</summary>
    public string? PinProtectedUserKey { get; init; }
    /// <summary>连续 PIN 解锁失败次数。达 5 清 PIN。</summary>
    public int PinFailedAttempts { get; init; }
    /// <summary>服务端"记住此设备"令牌(identity.rs:563 TwoFactorToken)。null = 未记住/未返回。
    /// 二次登录以 provider=Remember(5) 回传,免交互 2FA。</summary>
    public string? TwoFactorToken { get; init; }
    /// <summary>Windows Hello 派生密钥包住的 UserKey(EncString 文本)。null = 未启用 Hello。</summary>
    public string? HelloProtectedUserKey { get; init; }
    /// <summary>启用 Hello 时生成的随机挑战(base64)。解锁时对同一挑战重新签名。非机密。</summary>
    public string? HelloChallenge { get; init; }
}
