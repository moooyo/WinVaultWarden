using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record AccountEmailTokenRequest(
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash,
    [property: JsonPropertyName("newEmail")] string NewEmail);

public sealed record AccountEmailChangeRequest(
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash,
    [property: JsonPropertyName("newEmail")] string NewEmail,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("newMasterPasswordHash")] string NewMasterPasswordHash,
    [property: JsonPropertyName("token")] string Token);

public sealed record AccountEmailVerificationRequest(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("token")] string Token);

public sealed record AccountSecurityStampRequest(
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AccountEmailTokenRequest))]
[JsonSerializable(typeof(AccountEmailChangeRequest))]
[JsonSerializable(typeof(AccountEmailVerificationRequest))]
[JsonSerializable(typeof(AccountSecurityStampRequest))]
public partial class AccountManagementJsonContext : JsonSerializerContext;
