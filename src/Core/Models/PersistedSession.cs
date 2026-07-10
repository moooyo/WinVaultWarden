using System.Text.Json.Serialization;
using Core.Enums;

namespace Core.Models;

[method: JsonConstructor]
public sealed record PersistedSession(
    int SchemaVersion,
    string AccountId,
    ServerOrigin Origin,
    long Generation,
    string Email,
    string DeviceIdentifier,
    string RefreshToken,
    string ProtectedUserKey,
    KdfType KdfType,
    int KdfIterations,
    int? KdfMemory,
    int? KdfParallelism)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// 持久化 Generation 的安全上界。任何等于或超过此值的持久化记录将被 HasValidBinding 拒绝
    /// （fail-closed）。防止恶意/损坏记录将 _generation 恢复到 long.MaxValue，导致下一次
    /// Lock/Logout 在 RotateLeaseNoLock 内立即溢出（或即便采用饱和计数后仍无法继续递增）。
    /// 具体值为 long.MaxValue - 1：允许正常会话最多递增到 long.MaxValue（饱和），
    /// 而 long.MaxValue 本身则无法通过绑定校验被持久化恢复进来。
    /// </summary>
    public const long MaxPersistedGeneration = long.MaxValue - 1;

    /// <summary>
    /// fail-closed 门禁:任何缺失 account/origin/generation 绑定的持久化记录一律视为不可信,
    /// TryLoad 必须拒绝返回它(见 DpapiTokenStore/MemoryTokenStore),迫使用户重新登录以获得
    /// schema 2 记录,而不是把旧的、未绑定身份的 session 静默当作当前账户使用。
    /// Generation 额外受 MaxPersistedGeneration 上界约束，防止恢复后下一次 Lock/Logout 溢出。
    /// </summary>
    public bool HasValidBinding =>
        SchemaVersion == CurrentSchemaVersion
        && !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(Email)
        && Generation > 0
        && Generation <= MaxPersistedGeneration
        && Origin.IsInitialized;

    /// <summary>
    /// 兼容性只读视图:非 Task 2 范围内的调用点(AuthService/TokenRefreshService/NotificationsService 等)
    /// 仍以字符串形式使用服务端地址。与 AccountInfo.ServerUrl 同一约定,值等于 Origin 的规范化字符串。
    /// </summary>
    public string ServerUrl => Origin.Value;

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
