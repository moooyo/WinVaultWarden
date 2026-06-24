using System.Text.Json.Serialization;
using Core.Enums;

namespace Api.Dtos;

public sealed record PreloginRequest([property: JsonPropertyName("email")] string Email);

public sealed record PreloginResponse(
    [property: JsonPropertyName("kdf")] KdfType Kdf,
    [property: JsonPropertyName("kdfIterations")] int KdfIterations,
    [property: JsonPropertyName("kdfMemory")] int? KdfMemory,
    [property: JsonPropertyName("kdfParallelism")] int? KdfParallelism);
