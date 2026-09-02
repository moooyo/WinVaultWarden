using System.Text.Json.Serialization;

namespace Api.Dtos;

public sealed record ListResponse<T>(
    [property: JsonPropertyName("data")] T[] Data,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken,
    [property: JsonPropertyName("object")] string? Object);

public sealed record DeviceDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("creationDate")] DateTimeOffset? CreationDate,
    [property: JsonPropertyName("isTrusted")] bool IsTrusted,
    [property: JsonPropertyName("userId")] string? UserId = null,
    [property: JsonPropertyName("revisionDate")] DateTimeOffset? RevisionDate = null,
    [property: JsonPropertyName("encryptedUserKey")] string? EncryptedUserKey = null,
    [property: JsonPropertyName("encryptedPublicKey")] string? EncryptedPublicKey = null,
    [property: JsonPropertyName("devicePendingAuthRequest")] DevicePendingAuthRequestDto? DevicePendingAuthRequest = null,
    [property: JsonPropertyName("lastActivityDate")] DateTimeOffset? LastActivityDate = null);

public sealed record DevicePendingAuthRequestDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("creationDate")] DateTimeOffset CreationDate);

public sealed record TrustedDeviceKeysRequest(
    [property: JsonPropertyName("encryptedUserKey")] string EncryptedUserKey,
    [property: JsonPropertyName("encryptedPublicKey")] string EncryptedPublicKey,
    [property: JsonPropertyName("encryptedPrivateKey")] string EncryptedPrivateKey);

public sealed record UntrustDevicesRequest(
    [property: JsonPropertyName("devices")] string[] Devices);
