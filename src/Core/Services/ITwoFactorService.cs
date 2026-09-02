using Core.Models;

namespace Core.Services;

// Coordinates account-level two-factor management. Legacy providers use the current master
// password; WebAuthn accepts one explicit server-side verification proof.
public interface ITwoFactorService
{
    /// <summary>列出当前账户已配置的所有两步验证提供者（含启用/禁用状态）。</summary>
    Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default);

    /// <summary>
    /// 启动 TOTP 设置流程。返回 (secret, otpauth_url)，供前端生成二维码。
    /// </summary>
    Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default);

    /// <summary>
    /// Completes TOTP activation and returns the explicit commit state together
    /// with recovery-code availability.
    /// </summary>
    Task<TotpActivationResult> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default);

    /// <summary>Retrieves the recovery code without activating TOTP again.</summary>
    Task<string> GetRecoveryCodeAsync(string pw, CancellationToken ct = default);

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

    /// <summary>Returns the registered account-level WebAuthn second-factor credentials.</summary>
    Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(string pw, CancellationToken ct = default);

    /// <summary>Returns WebAuthn credentials after applying exactly one server-side proof.</summary>
    Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default);

    /// <summary>Requests an emailed protected-action OTP from a compatible server.</summary>
    Task RequestProtectedActionOtpAsync(CancellationToken ct = default);

    /// <summary>
    /// Requests and validates a server-bound, single-use WebAuthn credential creation challenge.
    /// </summary>
    Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        string pw,
        CancellationToken ct = default);

    Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default);

    /// <summary>
    /// Submits one authenticator attestation. A challenge can be submitted at most once, including
    /// when the server result is uncertain.
    /// </summary>
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

    /// <summary>
    /// Deletes one WebAuthn credential. Deleting the last credential disables the whole provider.
    /// </summary>
    Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        string pw,
        int id,
        CancellationToken ct = default);

    Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(
        TwoFactorUserVerification verification,
        int id,
        CancellationToken ct = default);

    /// <summary>
    /// 禁用指定类型的两步验证提供者。type 同 Bitwarden TwoFactorProviderType 整数值。
    /// </summary>
    Task DisableAsync(string pw, int type, CancellationToken ct = default);
}
