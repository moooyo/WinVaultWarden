using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record AccountKeysResponse(
    [property: JsonPropertyName("publicKeyEncryptionKeyPair")]
    PublicKeyEncryptionKeyPairResponse? PublicKeyEncryptionKeyPair,
    [property: JsonPropertyName("signatureKeyPair")]
    SignatureKeyPairResponse? SignatureKeyPair = null,
    [property: JsonPropertyName("securityState")]
    AccountSecurityState? SecurityState = null);

public sealed record PublicKeyEncryptionKeyPairResponse(
    [property: JsonPropertyName("wrappedPrivateKey")] string? WrappedPrivateKey,
    [property: JsonPropertyName("publicKey")] string? PublicKey = null,
    [property: JsonPropertyName("signedPublicKey")] string? SignedPublicKey = null,
    [property: JsonPropertyName("object")] string? Object = null);

public sealed record SignatureKeyPairResponse(
    [property: JsonPropertyName("wrappedSigningKey")] string? WrappedSigningKey,
    [property: JsonPropertyName("verifyingKey")] string? VerifyingKey = null,
    [property: JsonPropertyName("object")] string? Object = null);

public sealed record AccountSecurityState(
    [property: JsonPropertyName("securityState")] string? SecurityState,
    [property: JsonPropertyName("securityVersion")] int SecurityVersion);

public sealed record AccountKeysRequest(
    [property: JsonPropertyName("userKeyEncryptedAccountPrivateKey")]
    string UserKeyEncryptedAccountPrivateKey,
    [property: JsonPropertyName("accountPublicKey")]
    string AccountPublicKey,
    [property: JsonPropertyName("publicKeyEncryptionKeyPair")]
    PublicKeyEncryptionKeyPairRequest PublicKeyEncryptionKeyPair,
    [property: JsonPropertyName("signatureKeyPair")]
    SignatureKeyPairRequest SignatureKeyPair,
    [property: JsonPropertyName("securityState")]
    AccountSecurityState SecurityState);

public sealed record PublicKeyEncryptionKeyPairRequest(
    [property: JsonPropertyName("wrappedPrivateKey")] string WrappedPrivateKey,
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("signedPublicKey")] string SignedPublicKey);

public sealed record SignatureKeyPairRequest(
    [property: JsonPropertyName("signatureAlgorithm")] string SignatureAlgorithm,
    [property: JsonPropertyName("wrappedSigningKey")] string WrappedSigningKey,
    [property: JsonPropertyName("verifyingKey")] string VerifyingKey);

public sealed record PostAccountKeysRequest(
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("encryptedPrivateKey")] string EncryptedPrivateKey,
    [property: JsonPropertyName("accountKeys")] AccountKeysRequest AccountKeys,
    [property: JsonPropertyName("userKeyId")] string UserKeyId);

public sealed record PostAccountKeysResponse(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("publicKey")] string? PublicKey,
    [property: JsonPropertyName("privateKey")] string? PrivateKey,
    [property: JsonPropertyName("accountKeys")] AccountKeysResponse? AccountKeys,
    [property: JsonPropertyName("object")] string? Object = null);

public sealed record ResetPasswordEnrollmentRequest(
    [property: JsonPropertyName("resetPasswordKey")] string ResetPasswordKey,
    [property: JsonPropertyName("masterPasswordHash")] string? MasterPasswordHash);

public sealed record OrganizationAutoEnrollStatusResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("resetPasswordEnabled")] bool ResetPasswordEnabled,
    [property: JsonPropertyName("object")] string? Object = null);

public sealed record OrganizationKeysResponse(
    [property: JsonPropertyName("publicKey")] string? PublicKey,
    [property: JsonPropertyName("privateKey")] string? PrivateKey,
    [property: JsonPropertyName("object")] string? Object = null);
