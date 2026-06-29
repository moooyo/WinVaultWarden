using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record ProfileUpdateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("culture")] string Culture);

public sealed record ChangePasswordRequest(
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash,
    [property: JsonPropertyName("newMasterPasswordHash")] string NewMasterPasswordHash,
    [property: JsonPropertyName("masterPasswordHint")] string? MasterPasswordHint,
    [property: JsonPropertyName("key")] string Key);

public sealed record KdfParams(
    [property: JsonPropertyName("kdf")] int Kdf,
    [property: JsonPropertyName("kdfIterations")] int KdfIterations,
    [property: JsonPropertyName("kdfMemory")] int? KdfMemory,
    [property: JsonPropertyName("kdfParallelism")] int? KdfParallelism);

public sealed record AuthData(
    [property: JsonPropertyName("salt")] string Salt,
    [property: JsonPropertyName("kdf")] KdfParams Kdf,
    [property: JsonPropertyName("masterPasswordAuthenticationHash")] string MasterPasswordAuthenticationHash);

public sealed record UnlockData(
    [property: JsonPropertyName("salt")] string Salt,
    [property: JsonPropertyName("kdf")] KdfParams Kdf,
    [property: JsonPropertyName("masterKeyWrappedUserKey")] string MasterKeyWrappedUserKey);

public sealed record ChangeKdfRequest(
    [property: JsonPropertyName("newMasterPasswordHash")] string NewMasterPasswordHash,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("authenticationData")] AuthData AuthenticationData,
    [property: JsonPropertyName("unlockData")] UnlockData UnlockData,
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash);
