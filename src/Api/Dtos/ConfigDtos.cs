using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record ConfigResponse(
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("server")] string? Server);
