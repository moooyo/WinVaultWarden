using Core.Models;
using Core.Services;

namespace App.Services;

public interface ITwoFactorUiService
{
    /// <summary>列出当前账户已配置的所有两步验证提供者。</summary>
    Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default);

    /// <summary>启动 TOTP 设置流程，返回 (secret, otpauth_url)。</summary>
    Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default);

    /// <summary>Completes TOTP activation and returns its commit state and recovery-code availability.</summary>
    Task<TotpActivationResult> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default);

    /// <summary>Retrieves the recovery code without activating TOTP again.</summary>
    Task<string> GetRecoveryCodeAsync(string pw, CancellationToken ct = default);

    /// <summary>启动 Email 两步验证设置，发验证邮件到账户邮箱，返回脱敏邮箱或 null。</summary>
    Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default);

    /// <summary>向指定邮箱重发验证码。</summary>
    Task SendEmailAsync(string pw, string email, CancellationToken ct = default);

    /// <summary>完成 Email 两步验证启用。</summary>
    Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default);

    Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(
        string pw,
        CancellationToken ct = default);

    Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default);

    Task RequestProtectedActionOtpAsync(CancellationToken ct = default);

    Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        string pw,
        CancellationToken ct = default);

    Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default);

    Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(
        string pw,
        int id,
        string name,
        WebAuthnRegistrationChallenge challenge,
        WebAuthnRegistrationDeviceResponse deviceResponse,
        CancellationToken ct = default);

    Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(
        TwoFactorUserVerification verification,
        int id,
        string name,
        WebAuthnRegistrationChallenge challenge,
        WebAuthnRegistrationDeviceResponse deviceResponse,
        CancellationToken ct = default);

    Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        string pw,
        int id,
        CancellationToken ct = default);

    Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        TwoFactorUserVerification verification,
        int id,
        CancellationToken ct = default);

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

    public Task<TotpActivationResult> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default) =>
        Require().EnableTotpAsync(pw, secret, code, ct);

    public Task<string> GetRecoveryCodeAsync(string pw, CancellationToken ct = default) =>
        Require().GetRecoveryCodeAsync(pw, ct);

    public Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default) =>
        Require().BeginEmailSetupAsync(pw, ct);

    public Task SendEmailAsync(string pw, string email, CancellationToken ct = default) =>
        Require().SendEmailAsync(pw, email, ct);

    public Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default) =>
        Require().EnableEmailAsync(pw, email, token, ct);

    public Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(
        string pw,
        CancellationToken ct = default) =>
        Require().GetWebAuthnAsync(pw, ct);

    public Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default) =>
        Require().GetWebAuthnAsync(verification, ct);

    public Task RequestProtectedActionOtpAsync(CancellationToken ct = default) =>
        Require().RequestProtectedActionOtpAsync(ct);

    public Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        string pw,
        CancellationToken ct = default) =>
        Require().GetWebAuthnChallengeAsync(pw, ct);

    public Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default) =>
        Require().GetWebAuthnChallengeAsync(verification, ct);

    public Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(
        string pw,
        int id,
        string name,
        WebAuthnRegistrationChallenge challenge,
        WebAuthnRegistrationDeviceResponse deviceResponse,
        CancellationToken ct = default) =>
        Require().CompleteWebAuthnRegistrationAsync(
            pw,
            id,
            name,
            challenge,
            deviceResponse,
            ct);

    public Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(
        TwoFactorUserVerification verification,
        int id,
        string name,
        WebAuthnRegistrationChallenge challenge,
        WebAuthnRegistrationDeviceResponse deviceResponse,
        CancellationToken ct = default) =>
        Require().CompleteWebAuthnRegistrationAsync(
            verification,
            id,
            name,
            challenge,
            deviceResponse,
            ct);

    public Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        string pw,
        int id,
        CancellationToken ct = default) =>
        Require().DeleteWebAuthnCredentialAsync(pw, id, ct);

    public Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        TwoFactorUserVerification verification,
        int id,
        CancellationToken ct = default) =>
        Require().DeleteWebAuthnCredentialAsync(verification, id, ct);

    public Task DisableAsync(string pw, int type, CancellationToken ct = default) =>
        Require().DisableAsync(pw, type, ct);
}
