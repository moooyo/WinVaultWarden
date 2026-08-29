using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record ProfileUpdateRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("culture")] string Culture);

public sealed record ChangePasswordRequest(
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash,
    [property: JsonPropertyName("newMasterPasswordHash")] string NewMasterPasswordHash,
    [property: JsonPropertyName("masterPasswordHint")] string? MasterPasswordHint,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("authenticationData")] AuthData? AuthenticationData = null,
    [property: JsonPropertyName("unlockData")] UnlockData? UnlockData = null);

public sealed record KdfParams(
    [property: JsonPropertyName("kdfType")] int Kdf,
    [property: JsonPropertyName("iterations")] int KdfIterations,
    [property: JsonPropertyName("memory")] int? KdfMemory,
    [property: JsonPropertyName("parallelism")] int? KdfParallelism);

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

public sealed record RegisterKeys(
    [property: JsonPropertyName("encryptedPrivateKey")] string EncryptedPrivateKey,
    [property: JsonPropertyName("publicKey")] string PublicKey);

public sealed record RegisterVerificationRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("receiveMarketingEmails")] bool ReceiveMarketingEmails = false);

public sealed record RegisterFinishRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("emailVerificationToken")] string EmailVerificationToken,
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash,
    [property: JsonPropertyName("masterPasswordHint")] string? MasterPasswordHint,
    [property: JsonPropertyName("userSymmetricKey")] string UserSymmetricKey,
    [property: JsonPropertyName("kdf")] int Kdf,
    [property: JsonPropertyName("kdfIterations")] int KdfIterations,
    [property: JsonPropertyName("kdfMemory")] int? KdfMemory,
    [property: JsonPropertyName("kdfParallelism")] int? KdfParallelism,
    [property: JsonPropertyName("userAsymmetricKeys")] RegisterKeys UserAsymmetricKeys);

public sealed record RegisterRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash,
    [property: JsonPropertyName("masterPasswordHint")] string? MasterPasswordHint,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("kdf")] int Kdf,
    [property: JsonPropertyName("kdfIterations")] int KdfIterations,
    [property: JsonPropertyName("kdfMemory")] int? KdfMemory,
    [property: JsonPropertyName("kdfParallelism")] int? KdfParallelism,
    [property: JsonPropertyName("keys")] RegisterKeys Keys);
