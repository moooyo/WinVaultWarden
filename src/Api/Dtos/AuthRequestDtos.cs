using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Api.Dtos;

/// <summary>
/// Describes one authentication request returned by the server.
/// Official servers return the extended device metadata, while Vaultwarden
/// omits it; the optional members preserve compatibility with both shapes.
/// </summary>
public sealed record AuthRequestResponse(
    [property: JsonPropertyName("id")]                  string Id,
    [property: JsonPropertyName("publicKey")]           string PublicKey,
    [property: JsonPropertyName("requestDeviceType")]   string RequestDeviceType,
    [property: JsonPropertyName("requestIpAddress")]    string RequestIpAddress,
    [property: JsonPropertyName("key")]                 string? Key,
    [property: JsonPropertyName("masterPasswordHash")]  string? MasterPasswordHash,
    [property: JsonPropertyName("creationDate")]        string CreationDate,
    [property: JsonPropertyName("responseDate")]        string? ResponseDate,
    [property: JsonPropertyName("requestApproved")]     bool? RequestApproved,
    [property: JsonPropertyName("origin")]              string? Origin,
    [property: JsonPropertyName("requestDeviceIdentifier")] string? RequestDeviceIdentifier = null,
    [property: JsonPropertyName("requestDeviceTypeValue")] int? RequestDeviceTypeValue = null,
    [property: JsonPropertyName("requestCountryName")] string? RequestCountryName = null,
    [property: JsonPropertyName("requestDeviceId")] string? RequestDeviceId = null,
    [property: JsonPropertyName("object")] string? Object = null);

/// <summary>
/// Wraps the authentication request list returned by the API.
/// </summary>
public sealed record AuthRequestListResponse(
    [property: JsonPropertyName("data")] List<AuthRequestResponse> Data);

/// <summary>
/// Approves or rejects a pending authentication request.
/// </summary>
public sealed record AuthResponseRequest(
    [property: JsonPropertyName("deviceIdentifier")]    string DeviceIdentifier,
    [property: JsonPropertyName("key")]                 string Key,
    [property: JsonPropertyName("masterPasswordHash")]  string? MasterPasswordHash,
    [property: JsonPropertyName("requestApproved")]     bool RequestApproved);

/// <summary>
/// Creates a new authentication request from the device that is signing in.
/// </summary>
public sealed record AuthRequestRequest(
    [property: JsonPropertyName("accessCode")]          string AccessCode,
    [property: JsonPropertyName("deviceIdentifier")]    string DeviceIdentifier,
    [property: JsonPropertyName("email")]               string Email,
    [property: JsonPropertyName("publicKey")]           string PublicKey,
    [property: JsonPropertyName("type")]                int Type);
