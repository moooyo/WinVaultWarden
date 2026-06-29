namespace Core.Models;

/// <summary>等待审批的设备登录请求（auth-request）。</summary>
/// <param name="Id">服务端分配的 auth-request UUID。</param>
/// <param name="DeviceTypeName">发起方设备类型名称（如 "Windows"、"Android"）。</param>
/// <param name="IpAddress">发起方 IP 地址。</param>
/// <param name="CreationDate">请求创建时间（ISO 8601 字符串，服务端原样返回）。</param>
/// <param name="PublicKey">发起方公钥（Base64），批准时用于加密会话密钥。</param>
public record PendingAuthRequest(
    string Id,
    string DeviceTypeName,
    string IpAddress,
    string CreationDate,
    string PublicKey);
