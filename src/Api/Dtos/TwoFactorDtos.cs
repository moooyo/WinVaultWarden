using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Api.Dtos;

/// <summary>
/// 发送前先验证主密码（多处端点复用）。
/// POST /api/two-factor/get-authenticator 等。
/// </summary>
public sealed record PasswordVerifyRequest(
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash);

/// <summary>
/// 服务端返回的 TOTP 身份验证器状态与密钥。
/// GET /api/two-factor/get-authenticator 响应体。
/// </summary>
public sealed record AuthenticatorResponse(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("key")] string Key);

/// <summary>
/// 启用 TOTP 身份验证器。
/// POST /api/two-factor/authenticator。
/// </summary>
public sealed record EnableAuthenticatorRequest(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash);

/// <summary>
/// 停用指定类型的两步验证。
/// POST /api/two-factor/disable。
/// type: TwoFactorProviderType 整数值（0=Authenticator, 1=Email, …）。
/// </summary>
public sealed record DisableTwoFactorRequest(
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash,
    [property: JsonPropertyName("type")] int Type);

/// <summary>
/// 邮箱两步验证的启用状态。
/// GET /api/two-factor/get-email 响应体。
/// </summary>
public sealed record EmailStatusResponse(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// 请求发送验证码到指定邮箱。
/// POST /api/two-factor/send-email-login 或 /api/two-factor/send-email。
/// </summary>
public sealed record SendEmailRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash);

/// <summary>
/// 验证邮箱验证码并启用邮箱两步验证。
/// POST /api/two-factor/email。
/// </summary>
public sealed record EmailVerifyRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("masterPasswordHash")] string MasterPasswordHash);

/// <summary>
/// 恢复码（用于重置两步验证）。
/// GET /api/two-factor/get-recover 响应体。
/// </summary>
public sealed record RecoverResponse(
    [property: JsonPropertyName("code")] string? Code);

/// <summary>
/// 单个两步验证提供者的类型与启用状态。
/// Vaultwarden TwoFactorProvider.to_json() 对应结构。
/// </summary>
public sealed record TwoFactorProviderItem(
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// 账户所有两步验证提供者列表。
/// GET /api/two-factor 响应体，"data" 字段名与 Vaultwarden ListResponse 约定一致。
/// </summary>
public sealed record TwoFactorProvidersResponse(
    [property: JsonPropertyName("data")] List<TwoFactorProviderItem> Data);
