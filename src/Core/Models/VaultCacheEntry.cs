using System.Text.Json.Serialization;

namespace Core.Models;

/// <summary>
/// 落盘的密码库缓存条目(纯数据)。SyncResponseJson 是**加密态** SyncResponse 的原样 JSON
/// (字段皆 EncString 密文)。头部字段(schema/email/serverUrl)明文,用于失效判断与"数据可能过期"提示。
/// 由 ICacheStore 序列化 + DPAPI 保护后落盘。
/// </summary>
[method: JsonConstructor]
public sealed record VaultCacheEntry(
    int SchemaVersion,
    string AccountId,
    ServerOrigin Origin,
    long Generation,
    string Email,
    DateTimeOffset CachedAtUtc,
    string SyncResponseJson)
{
    /// <summary>
    /// 兼容性只读视图:VaultCacheService(非 Task 2 范围)仍以字符串形式比较服务端地址。
    /// 与 PersistedSession.ServerUrl/AccountInfo.ServerUrl 同一约定。
    /// </summary>
    public string ServerUrl => Origin.Value;

    /// <summary>
    /// 过渡构造:Task 2 引入 account/origin/generation 绑定之前遗留的调用点仍以此签名构造。
    /// 只保留到 Task 6(缓存 lease 迁移完成),最终门禁禁止仓库中残留任何 [Obsolete] 构造。
    /// </summary>
    [Obsolete("Task 6 removes this constructor after cache lease migration.")]
    public VaultCacheEntry(
        int schemaVersion,
        string email,
        string serverUrl,
        DateTimeOffset cachedAtUtc,
        string syncResponseJson)
        : this(
            schemaVersion,
            email,
            new ServerOrigin(serverUrl),
            1,
            email,
            cachedAtUtc,
            syncResponseJson)
    {
    }
}
