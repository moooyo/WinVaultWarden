namespace App.ViewModels.Models;

/// <summary>
/// 待审批的设备登录请求视图模型项。
/// </summary>
/// <param name="Id">auth-request UUID。</param>
/// <param name="DeviceTypeName">发起方设备类型名称，如 "Windows"、"Android"。</param>
/// <param name="IpAddress">发起方 IP 地址。</param>
/// <param name="CreatedLabel">格式化后的创建时间文本，供 UI 直接展示。</param>
/// <param name="PublicKey">发起方公钥（Base64），批准时用于加密会话密钥。</param>
public sealed record AuthRequestItem(
    string Id,
    string DeviceTypeName,
    string IpAddress,
    string CreatedLabel,
    string PublicKey);
