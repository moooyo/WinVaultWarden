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
    /// fail-closed 门禁:任何缺失 account/origin/generation 绑定的持久化记录一律视为不可信,
    /// TryLoad 必须拒绝返回它(见 DpapiTokenStore/MemoryTokenStore),迫使用户重新登录以获得
    /// schema 2 记录,而不是把旧的、未绑定身份的 session 静默当作当前账户使用。
    /// </summary>
    public bool HasValidBinding =>
        SchemaVersion == CurrentSchemaVersion
        && !string.IsNullOrWhiteSpace(AccountId)
        && !string.IsNullOrWhiteSpace(Email)
        && Generation > 0
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

    /// <summary>
    /// 过渡构造:Task 2 引入 account/origin/generation 绑定之前遗留的调用点仍以此签名构造。
    /// 以 email 作为不可信过渡 AccountId/绑定占位,generation 固定为 1(非真实 generation)。
    /// Task 7 完成 JWT subject 迁移、所有调用点获得真实 account ID/generation 后必须删除此构造函数,
    /// 门禁禁止仓库中残留任何 [Obsolete] 构造。
    /// </summary>
    [Obsolete("Task 7 removes this constructor after JWT subject migration.")]
    public PersistedSession(
        string serverUrl,
        string email,
        string deviceIdentifier,
        string refreshToken,
        string protectedUserKey,
        KdfType kdfType,
        int kdfIterations,
        int? kdfMemory,
        int? kdfParallelism)
        : this(
            CurrentSchemaVersion,
            email,
            new ServerOrigin(serverUrl),
            1,
            email,
            deviceIdentifier,
            refreshToken,
            protectedUserKey,
            kdfType,
            kdfIterations,
            kdfMemory,
            kdfParallelism)
    {
    }
}
