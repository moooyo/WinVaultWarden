using System.Text.Json.Serialization;

namespace Api.Dtos;

// GET /api/config 响应。`server` 是对象(name/url),不是字符串——
// 见 vaultwarden src/api/core/mod.rs `fn config()`。
public sealed record ConfigResponse(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("gitHash")] string? GitHash,
    [property: JsonPropertyName("server")] ServerInfo? Server);

public sealed record ServerInfo(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string? Url);
