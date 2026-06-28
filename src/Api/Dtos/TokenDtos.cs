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
    [property: JsonPropertyName("KdfParallelism")] int? KdfParallelism = null);

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
    string? RefreshToken)
{
    public static ConnectTokenRequest Password(
        string username,
        string password,
        string deviceIdentifier,
        string deviceName,
        TwoFactorPayload? twoFactor) =>
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
            null);

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
}

public sealed record TwoFactorPayload(int Provider, string Token, bool Remember);

public abstract record ConnectTokenResult
{
    public sealed record Success(TokenResponse Token) : ConnectTokenResult;
    public sealed record TwoFactorRequired(IReadOnlyList<int> Providers) : ConnectTokenResult;
    public sealed record Error(string Message) : ConnectTokenResult;
}

public sealed record ConnectTokenErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [property: JsonPropertyName("TwoFactorProviders")] int[]? TwoFactorProviders);
