using System.Text.Json.Serialization;

namespace Api.Dtos;

// GET /api/sync 顶层骨架(仅占位字段)
public sealed record SyncResponse(
    [property: JsonPropertyName("object")] string Object);
