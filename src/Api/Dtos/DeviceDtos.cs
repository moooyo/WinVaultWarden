using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record ListResponse<T>(
    [property: JsonPropertyName("data")] T[] Data,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken,
    [property: JsonPropertyName("object")] string? Object);

public sealed record DeviceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("creationDate")] DateTimeOffset? CreationDate,
    [property: JsonPropertyName("isTrusted")] bool IsTrusted);
