namespace Core.Models;

/// <summary>
/// 落盘的密码库缓存条目(纯数据)。SyncResponseJson 是**加密态** SyncResponse 的原样 JSON
/// (字段皆 EncString 密文)。头部字段(schema/email/serverUrl)明文,用于失效判断与"数据可能过期"提示。
/// 由 ICacheStore 序列化 + DPAPI 保护后落盘。
/// </summary>
public sealed record VaultCacheEntry(
    int SchemaVersion,
    string Email,
    string ServerUrl,
    DateTimeOffset CachedAtUtc,
    string SyncResponseJson);
