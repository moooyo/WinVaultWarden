using System.Text.Json;
using System.Text.Json.Serialization;
using Core.Enums;

namespace Api.Dtos;

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("Key")] string? Key,
    [property: JsonPropertyName("PrivateKey")] string? PrivateKey,
    [property: JsonPropertyName("Kdf")] KdfType Kdf,
    [property: JsonPropertyName("KdfIterations")] int KdfIterations,
    [property: JsonPropertyName("KdfMemory")] int? KdfMemory = null,
    [property: JsonPropertyName("KdfParallelism")] int? KdfParallelism = null,
    [property: JsonPropertyName("TwoFactorToken")] string? TwoFactorToken = null,
    [property: JsonPropertyName("UserDecryptionOptions")] UserDecryptionOptionsResponse? UserDecryptionOptions = null,
    [property: JsonPropertyName("AccountKeys")] AccountKeysResponse? AccountKeys = null,
    [property: JsonPropertyName("ForcePasswordReset")] bool ForcePasswordReset = false,
    [property: JsonPropertyName("MasterPasswordPolicy")] MasterPasswordPolicyResponse? MasterPasswordPolicy = null);

public sealed record MasterPasswordPolicyResponse(
    [property: JsonPropertyName("MinComplexity")] int? MinComplexity = null,
    [property: JsonPropertyName("MinLength")] int? MinLength = null,
    [property: JsonPropertyName("RequireUpper")] bool? RequireUpper = null,
    [property: JsonPropertyName("RequireLower")] bool? RequireLower = null,
    [property: JsonPropertyName("RequireNumbers")] bool? RequireNumbers = null,
    [property: JsonPropertyName("RequireSpecial")] bool? RequireSpecial = null,
    [property: JsonPropertyName("EnforceOnLogin")] bool? EnforceOnLogin = null,
    [property: JsonPropertyName("Object")] string? Object = null);

public sealed record UserDecryptionOptionsResponse(
    [property: JsonPropertyName("HasMasterPassword")] bool? HasMasterPassword,
    [property: JsonPropertyName("MasterPasswordUnlock")] MasterPasswordUnlockResponse? MasterPasswordUnlock = null,
    [property: JsonPropertyName("TrustedDeviceOption")] TrustedDeviceUserDecryptionOptionResponse? TrustedDeviceOption = null,
    [property: JsonPropertyName("KeyConnectorOption")] KeyConnectorUserDecryptionOptionResponse? KeyConnectorOption = null);

public sealed record TrustedDeviceUserDecryptionOptionResponse(
    [property: JsonPropertyName("HasAdminApproval")] bool? HasAdminApproval = null,
    [property: JsonPropertyName("HasLoginApprovingDevice")] bool? HasLoginApprovingDevice = null,
    [property: JsonPropertyName("HasManageResetPasswordPermission")] bool? HasManageResetPasswordPermission = null,
    [property: JsonPropertyName("IsTdeOffboarding")] bool? IsTdeOffboarding = null,
    [property: JsonPropertyName("EncryptedPrivateKey")] string? EncryptedPrivateKey = null,
    [property: JsonPropertyName("EncryptedUserKey")] string? EncryptedUserKey = null);

public sealed record KeyConnectorUserDecryptionOptionResponse(
    [property: JsonPropertyName("KeyConnectorUrl")] string? KeyConnectorUrl);

public sealed record MasterPasswordUnlockResponse(
    [property: JsonPropertyName("Kdf")] PreloginKdfSettingsResponse? Kdf,
    [property: JsonPropertyName("MasterKeyEncryptedUserKey")] string? MasterKeyEncryptedUserKey,
    [property: JsonPropertyName("MasterKeyWrappedUserKey")] string? MasterKeyWrappedUserKey,
    [property: JsonPropertyName("Salt")] string? Salt,
    [property: JsonPropertyName("ContainedKeyId")] string? ContainedKeyId = null);

public sealed record ConnectTokenRequest(
    string GrantType,
    string? Username,
    string? PasswordHash,
    string Scope,
    string ClientId,
    string DeviceIdentifier,
    string DeviceName,
    string DeviceType,
    TwoFactorPayload? TwoFactor,
    string? RefreshToken,
    string? Code = null,
    string? CodeVerifier = null,
    string? RedirectUri = null,
    string? NewDeviceOtp = null,
    string? AuthRequestId = null)
{
    public static ConnectTokenRequest Password(
        string username,
        string password,
        string deviceIdentifier,
        string deviceName,
        TwoFactorPayload? twoFactor,
        string? newDeviceOtp = null) =>
        new(
            "password",
            username,
            password,
            "api offline_access",
            "desktop",
            deviceIdentifier,
            deviceName,
            "6",
            twoFactor,
            null,
            NewDeviceOtp: newDeviceOtp);

    public static ConnectTokenRequest AuthRequest(
        string username,
        string accessCode,
        string authRequestId,
        string deviceIdentifier,
        string deviceName,
        TwoFactorPayload? twoFactor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        if (accessCode.Length != 25)
        {
            throw new ArgumentException(
                "An auth-request access code must contain exactly 25 characters.",
                nameof(accessCode));
        }

        return new ConnectTokenRequest(
            "password",
            username,
            accessCode,
            "api offline_access",
            "desktop",
            NormalizeRequiredGuid(deviceIdentifier, nameof(deviceIdentifier)),
            deviceName,
            "6",
            twoFactor,
            null,
            AuthRequestId: NormalizeRequiredGuid(authRequestId, nameof(authRequestId)));
    }

    public static ConnectTokenRequest Refresh(string refreshToken, string deviceIdentifier, string deviceName) =>
        new(
            "refresh_token",
            null,
            null,
            "api offline_access",
            "desktop",
            deviceIdentifier,
            deviceName,
            "6",
            null,
            refreshToken);

    public static ConnectTokenRequest AuthorizationCode(
        string code,
        string codeVerifier,
        string redirectUri,
        string deviceIdentifier,
        string deviceName,
        TwoFactorPayload? twoFactor = null) =>
        new(
            "authorization_code",
            null,
            null,
            "api offline_access",
            "desktop",
            deviceIdentifier,
            deviceName,
            "6",
            twoFactor,
            null,
            code,
            codeVerifier,
            redirectUri);

    private static string NormalizeRequiredGuid(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Guid.TryParseExact(value, "D", out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException(
                "The value must be a non-empty GUID in canonical D format.",
                parameterName);
        }

        return parsed.ToString("D");
    }
}

public sealed record TwoFactorPayload(int Provider, string Token, bool Remember);

public abstract record ConnectTokenResult
{
    public sealed record Success(TokenResponse Token) : ConnectTokenResult;
    public sealed record TwoFactorRequired(
        IReadOnlyList<int> Providers,
        string? SsoEmail2FaSessionToken = null,
        string? Email = null,
        IReadOnlyDictionary<string, JsonElement?>? ProviderData = null) : ConnectTokenResult;
    public sealed record DeviceVerificationRequired : ConnectTokenResult;
    public sealed record Error(string Message, int? HttpStatus = null, string? ErrorCode = null) : ConnectTokenResult;
}

public sealed record ConnectTokenErrorModel(
    [property: JsonPropertyName("Message")] string? Message);

public sealed record ConnectTokenErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [property: JsonPropertyName("TwoFactorProviders")] int[]? TwoFactorProviders,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("SsoEmail2faSessionToken")] string? SsoEmail2FaSessionToken = null,
    [property: JsonPropertyName("Email")] string? Email = null,
    [property: JsonPropertyName("TwoFactorProviders2")] Dictionary<string, JsonElement?>? TwoFactorProviders2 = null,
    [property: JsonPropertyName("ErrorModel")] ConnectTokenErrorModel? ErrorModel = null);
