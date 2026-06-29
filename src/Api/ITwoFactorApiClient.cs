using Api.Dtos;

namespace Api;

// 两步验证管理端点（全部走 /api/two-factor 前缀）。
// 读取操作用 POST + masterPasswordHash 验证身份；
// 启用/禁用操作返回更新后的状态或空 200；
// 失败抛 VaultWriteException。
public interface ITwoFactorApiClient
{
    void SetBaseAddress(string baseUrl);

    // GET api/two-factor — 获取账户所有已配置提供者的列表与启用状态。
    Task<TwoFactorProvidersResponse> GetProvidersAsync(CancellationToken ct = default);

    // POST api/two-factor/get-authenticator — 获取 TOTP 密钥与当前启用状态，需主密码哈希。
    Task<AuthenticatorResponse> GetAuthenticatorAsync(PasswordVerifyRequest request, CancellationToken ct = default);

    // POST api/two-factor/authenticator — 启用 TOTP 身份验证器，返回更新后的状态。
    Task<AuthenticatorResponse> EnableAuthenticatorAsync(EnableAuthenticatorRequest request, CancellationToken ct = default);

    // DELETE api/two-factor/authenticator（含请求体）— 禁用 TOTP 身份验证器。
    Task DisableAuthenticatorAsync(DisableTwoFactorRequest request, CancellationToken ct = default);

    // POST api/two-factor/get-email — 获取邮箱两步验证的状态，需主密码哈希。
    Task<EmailStatusResponse> GetEmailAsync(PasswordVerifyRequest request, CancellationToken ct = default);

    // POST api/two-factor/send-email — 发送验证码到指定邮箱。
    Task SendEmailAsync(SendEmailRequest request, CancellationToken ct = default);

    // PUT api/two-factor/email — 验证邮箱验证码并启用邮箱两步验证，返回更新后的状态。
    Task<EmailStatusResponse> EnableEmailAsync(EmailVerifyRequest request, CancellationToken ct = default);

    // POST api/two-factor/get-recover — 获取恢复码，需主密码哈希。
    Task<RecoverResponse> GetRecoverAsync(PasswordVerifyRequest request, CancellationToken ct = default);

    // POST api/two-factor/disable — 禁用指定类型的两步验证。
    Task DisableAsync(DisableTwoFactorRequest request, CancellationToken ct = default);
}
