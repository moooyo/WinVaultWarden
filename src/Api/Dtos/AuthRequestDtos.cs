using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Api.Dtos;

/// <summary>
/// 服务端返回的单个 auth-request 详情。
/// GET /api/auth-requests/{id} 及 GET /api/auth-requests 列表项。
/// Vaultwarden: src/api/core/accounts.rs  AuthRequest::to_json()
/// </summary>
public sealed record AuthRequestResponse(
    [property: JsonPropertyName("id")]                  string Id,
    [property: JsonPropertyName("publicKey")]           string PublicKey,
    [property: JsonPropertyName("requestDeviceType")]   int RequestDeviceType,
    [property: JsonPropertyName("requestIpAddress")]    string RequestIpAddress,
    [property: JsonPropertyName("key")]                 string? Key,
    [property: JsonPropertyName("masterPasswordHash")]  string? MasterPasswordHash,
    [property: JsonPropertyName("creationDate")]        string CreationDate,
    [property: JsonPropertyName("responseDate")]        string? ResponseDate,
    [property: JsonPropertyName("requestApproved")]     bool? RequestApproved,
    [property: JsonPropertyName("origin")]              string? Origin);

/// <summary>
/// GET /api/auth-requests 的分页/列表包装。
/// Vaultwarden ListResponse 约定，"data" 字段名固定。
/// </summary>
public sealed record AuthRequestListResponse(
    [property: JsonPropertyName("data")] List<AuthRequestResponse> Data);

/// <summary>
/// 批准或拒绝一个待处理的 auth-request 时发送的响应体。
/// PUT /api/auth-requests/{id}
/// Vaultwarden: AuthRequestUpdateData / RespondAuthRequestData
/// </summary>
public sealed record AuthResponseRequest(
    [property: JsonPropertyName("deviceIdentifier")]    string DeviceIdentifier,
    [property: JsonPropertyName("key")]                 string Key,
    [property: JsonPropertyName("masterPasswordHash")]  string? MasterPasswordHash,
    [property: JsonPropertyName("requestApproved")]     bool RequestApproved);

/// <summary>
/// 创建新的 auth-request（无密码登录发起方）。
/// POST /api/auth-requests
/// Vaultwarden: NewAuthRequestData
/// </summary>
public sealed record AuthRequestRequest(
    [property: JsonPropertyName("accessCode")]          string AccessCode,
    [property: JsonPropertyName("deviceIdentifier")]    string DeviceIdentifier,
    [property: JsonPropertyName("email")]               string Email,
    [property: JsonPropertyName("publicKey")]           string PublicKey);
