using System.ComponentModel;

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
    string PublicKey) : INotifyPropertyChanged
{
    /// <summary>Bitwarden 指纹短语(5 词以 '-' 连接),审批前供用户与请求设备核对。</summary>
    public string FingerprintPhrase { get; init; } = "";

    /// <summary>The authoritative server creation time; missing values cannot be approved.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt => CreatedAt?.ToUniversalTime() is { } created
        && created <= DateTimeOffset.MaxValue.AddMinutes(-15)
        ? created.AddMinutes(15) : null;

    public string ExpirationLabel
    {
        get
        {
            if (ExpiresAt is not { } expiry)
                return "无法确认过期时间";
            var seconds = (long)Math.Ceiling((expiry - DateTimeOffset.UtcNow).TotalSeconds);
            return seconds > 0 ? $"{seconds / 60}:{seconds % 60:00} 后过期" : "已过期";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshExpiration() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpirationLabel)));
}
