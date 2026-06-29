using Core.Models;

namespace Core.Services;

// 两步验证管理编排。所有写操作需提供当前主密码用于服务端验证。
// 启用/禁用成功后返回更新后的 RecoveryCode（服务端在响应中下发）。
public interface ITwoFactorService
{
    /// <summary>列出当前账户已配置的所有两步验证提供者（含启用/禁用状态）。</summary>
    Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default);

    /// <summary>
    /// 启动 TOTP 设置流程。返回 (secret, otpauth_url)，供前端生成二维码。
    /// </summary>
    Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default);

    /// <summary>
    /// 完成 TOTP 启用：提交主密码、secret、当前 TOTP code。
    /// 返回最新 Recovery Code（服务端下发）。
    /// </summary>
    Task<string> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default);

    /// <summary>
    /// 启动 Email 两步验证设置流程：服务端发送验证邮件到账户邮箱。
    /// 返回已脱敏的目标邮箱（服务端返回），若服务端不返回则为 null。
    /// </summary>
    Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default);

    /// <summary>向指定邮箱重发验证码（用于 Email 两步验证设置中途换地址或重发）。</summary>
    Task SendEmailAsync(string pw, string email, CancellationToken ct = default);

    /// <summary>
    /// 完成 Email 两步验证启用：提交主密码、目标邮箱、收到的 token。
    /// </summary>
    Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default);

    /// <summary>
    /// 禁用指定类型的两步验证提供者。type 同 Bitwarden TwoFactorProviderType 整数值。
    /// </summary>
    Task DisableAsync(string pw, int type, CancellationToken ct = default);
}
