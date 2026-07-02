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
}
