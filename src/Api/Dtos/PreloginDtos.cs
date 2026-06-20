using System.Text.Json.Serialization;

namespace Api.Dtos;

// POST /identity/accounts/prelogin
public sealed record PreloginRequest([property: JsonPropertyName("email")] string Email);

// 响应:全小写 kdf 字段
public sealed record PreloginResponse(
    [property: JsonPropertyName("kdf")] int Kdf,
    [property: JsonPropertyName("kdfIterations")] int KdfIterations,
    [property: JsonPropertyName("kdfMemory")] int? KdfMemory,
    [property: JsonPropertyName("kdfParallelism")] int? KdfParallelism);
