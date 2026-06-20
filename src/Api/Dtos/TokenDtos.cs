using System.Text.Json.Serialization;

namespace Api.Dtos;

// POST /identity/connect/token 成功响应(部分关键字段,PascalCase)
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("Key")] string? Key,
    [property: JsonPropertyName("PrivateKey")] string? PrivateKey,
    [property: JsonPropertyName("Kdf")] int Kdf,
    [property: JsonPropertyName("KdfIterations")] int KdfIterations);
