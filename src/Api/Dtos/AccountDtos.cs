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
    [property: JsonPropertyName("masterKeyWrappedUserKey")] string MasterKeyWrappedUserKey,
    [property: JsonPropertyName("containedKeyId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ContainedKeyId = null);

public sealed record UpdateTemporaryPasswordRequest(
    [property: JsonPropertyName("authenticationData")] AuthData AuthenticationData,
    [property: JsonPropertyName("unlockData")] UnlockData UnlockData,
    [property: JsonPropertyName("masterPasswordHint")] string? MasterPasswordHint)
{
    [JsonPropertyName("newMasterPasswordHash")]
    public string NewMasterPasswordHash => AuthenticationData.MasterPasswordAuthenticationHash;

    [JsonPropertyName("key")]
    public string Key => UnlockData.MasterKeyWrappedUserKey;
}

public sealed record ChangeKdfRequest(
    [property: JsonPropertyName("newMasterPasswordHash")] string NewMasterPasswordHash,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("authenticationData")] AuthData AuthenticationData,
    [property: JsonPropertyName("unlockData")] UnlockData UnlockData,
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash);

public sealed record DeleteAccountRequest
{
    private const int MaxMasterPasswordHashLength = 300;
    private const int MaxOtpLength = 64;

    [JsonPropertyName("masterPasswordHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MasterPasswordHash { get; }

    [JsonPropertyName("otp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Otp { get; }

    [JsonConstructor]
    public DeleteAccountRequest(string? masterPasswordHash, string? otp)
    {
        if ((masterPasswordHash is null) == (otp is null))
        {
            throw new ArgumentException(
                "Exactly one account deletion verification field must be provided.");
        }

        MasterPasswordHash = masterPasswordHash is null
            ? null
            : Normalize(masterPasswordHash, MaxMasterPasswordHashLength, nameof(masterPasswordHash));
        Otp = otp is null
            ? null
            : Normalize(otp, MaxOtpLength, nameof(otp));
    }

    public static DeleteAccountRequest FromMasterPasswordHash(string value) =>
        new(value, null);

    public static DeleteAccountRequest FromOtp(string value) =>
        new(null, value);

    private static string Normalize(string value, int maximumLength, string parameterName)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("The verification value cannot be empty.", parameterName);
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The verification value cannot exceed {maximumLength} characters.",
                parameterName);
        }
        return normalized;
    }
}

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
