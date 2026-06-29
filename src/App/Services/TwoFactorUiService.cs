using Core.Models;
using Core.Services;

namespace App.Services;

public interface ITwoFactorUiService
{
    /// <summary>列出当前账户已配置的所有两步验证提供者。</summary>
    Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default);

    /// <summary>启动 TOTP 设置流程，返回 (secret, otpauth_url)。</summary>
    Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default);

    /// <summary>完成 TOTP 启用，返回 RecoveryCode。</summary>
    Task<string> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default);

    /// <summary>启动 Email 两步验证设置，发验证邮件到账户邮箱，返回脱敏邮箱或 null。</summary>
    Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default);

    /// <summary>向指定邮箱重发验证码。</summary>
    Task SendEmailAsync(string pw, string email, CancellationToken ct = default);

    /// <summary>完成 Email 两步验证启用。</summary>
    Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default);

    /// <summary>禁用指定类型的两步验证提供者。</summary>
    Task DisableAsync(string pw, int type, CancellationToken ct = default);
}

/// <summary>
/// App 层两步验证 UI 服务，委托到 Core.Services.ITwoFactorService。
/// 若注入的 ITwoFactorService 为 null，抛出 TwoFactorOperationException（SettingsViewModel 负责捕获并写 OperationError）。
/// </summary>
public sealed class TwoFactorUiService : ITwoFactorUiService
{
    private readonly ITwoFactorService? _twoFactor;

    public TwoFactorUiService(ITwoFactorService? twoFactor = null)
    {
        _twoFactor = twoFactor;
    }

    private ITwoFactorService Require() =>
        _twoFactor ?? throw new TwoFactorOperationException("两步验证服务不可用");

    public Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default) =>
        Require().ListProvidersAsync(ct);

    public Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default) =>
        Require().BeginTotpSetupAsync(pw, ct);

    public Task<string> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default) =>
        Require().EnableTotpAsync(pw, secret, code, ct);

    public Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default) =>
        Require().BeginEmailSetupAsync(pw, ct);

    public Task SendEmailAsync(string pw, string email, CancellationToken ct = default) =>
        Require().SendEmailAsync(pw, email, ct);

    public Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default) =>
        Require().EnableEmailAsync(pw, email, token, ct);

    public Task DisableAsync(string pw, int type, CancellationToken ct = default) =>
        Require().DisableAsync(pw, type, ct);
}
