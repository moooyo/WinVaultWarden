using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

/// <summary>
/// 两步验证管理编排层，实现 <see cref="ITwoFactorService"/>。
/// <para>
/// 所有写操作（BeginTotpSetup / EnableTotp / BeginEmailSetup / SendEmail / EnableEmail / Disable）
/// 均需提供当前主密码。服务内部从持久化会话读取 Email + KDF 参数，派生主密钥哈希后发给服务端验证。
/// 主密钥在 finally 中无条件清零，不落盘、不写日志。
/// </para>
/// <para>
/// 注意：此服务不需要 vault 已解锁（不依赖 VaultSession.UserKey），
/// 因为两步验证管理走的是 /api/two-factor 系列端点，服务端用 masterPasswordHash 验证身份即可。
/// </para>
/// </summary>
public sealed class TwoFactorService : ITwoFactorService
{
    private readonly CryptoService _crypto;
    private readonly ITwoFactorApiClient _api;
    private readonly ITokenStore _tokenStore;

    public TwoFactorService(
        CryptoService crypto,
        ITwoFactorApiClient api,
        ITokenStore tokenStore)
    {
        _crypto = crypto;
        _api = api;
        _tokenStore = tokenStore;
    }

    // -----------------------------------------------------------------------
    // ListProvidersAsync — 不需要主密码，直接拉取提供者列表
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default)
    {
        var response = await _api.GetProvidersAsync(ct);
        return response.Data
            .Select(item => new TwoFactorProvider(item.Type, item.Enabled))
            .ToList()
            .AsReadOnly();
    }

    // -----------------------------------------------------------------------
    // BeginTotpSetupAsync — 拿到服务端已存储的（或新生成的）TOTP 密钥，构造 otpauth URI
    // -----------------------------------------------------------------------

    public async Task<(string secret, string otpauth)> BeginTotpSetupAsync(
        string pw, CancellationToken ct = default)
    {
        var mphash = ComputeHash(pw); // 若 store 未加载会抛 TwoFactorOperationException
        var response = await _api.GetAuthenticatorAsync(new PasswordVerifyRequest(mphash), ct);

        var secret = response.Key;
        var email = LoadPersisted().Email;
        var otpauth = $"otpauth://totp/{Uri.EscapeDataString(email)}?secret={Uri.EscapeDataString(secret)}&issuer=WinVaultWarden";
        return (secret, otpauth);
    }

    // -----------------------------------------------------------------------
    // EnableTotpAsync — 把 secret(=key) + code(=token) + hash 发给服务端启用，再拉 recovery code
    // -----------------------------------------------------------------------

    public async Task<string> EnableTotpAsync(
        string pw, string secret, string code, CancellationToken ct = default)
    {
        var mphash = ComputeHash(pw);

        await _api.EnableAuthenticatorAsync(
            new EnableAuthenticatorRequest(Key: secret, Token: code, MasterPasswordHash: mphash), ct);

        var recover = await _api.GetRecoverAsync(new PasswordVerifyRequest(mphash), ct);
        return recover.Code ?? string.Empty;
    }

    // -----------------------------------------------------------------------
    // BeginEmailSetupAsync — 拿到服务端返回的目标邮箱（脱敏版本）
    // -----------------------------------------------------------------------

    public async Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default)
    {
        var mphash = ComputeHash(pw);
        var response = await _api.GetEmailAsync(new PasswordVerifyRequest(mphash), ct);
        return response.Email;
    }

    // -----------------------------------------------------------------------
    // SendEmailAsync — 触发服务端向指定邮箱发送验证码
    // -----------------------------------------------------------------------

    public async Task SendEmailAsync(string pw, string email, CancellationToken ct = default)
    {
        var mphash = ComputeHash(pw);
        await _api.SendEmailAsync(new SendEmailRequest(Email: email, MasterPasswordHash: mphash), ct);
    }

    // -----------------------------------------------------------------------
    // EnableEmailAsync — 提交邮箱 token 完成 Email 两步验证启用
    // -----------------------------------------------------------------------

    public async Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
    {
        var mphash = ComputeHash(pw);
        await _api.EnableEmailAsync(
            new EmailVerifyRequest(Email: email, Token: token, MasterPasswordHash: mphash), ct);
    }

    // -----------------------------------------------------------------------
    // DisableAsync — 禁用指定类型的两步验证提供者
    // -----------------------------------------------------------------------

    public async Task DisableAsync(string pw, int type, CancellationToken ct = default)
    {
        var mphash = ComputeHash(pw);
        await _api.DisableAsync(new DisableTwoFactorRequest(MasterPasswordHash: mphash, Type: type), ct);
    }

    // -----------------------------------------------------------------------
    // 私有辅助
    // -----------------------------------------------------------------------

    /// <summary>
    /// 加载持久化会话，失败则抛 <see cref="TwoFactorOperationException"/>。
    /// </summary>
    private Core.Models.PersistedSession LoadPersisted()
    {
        if (!_tokenStore.TryLoad(out var persisted))
            throw new TwoFactorOperationException("无法加载持久化会话，请重新登录后重试。");
        return persisted;
    }

    /// <summary>
    /// 从持久化会话派生 masterKey，计算 masterPasswordHash，然后无条件清零 masterKey。
    /// </summary>
    private string ComputeHash(string pw)
    {
        var persisted = LoadPersisted();

        var mk = _crypto.DeriveMasterKey(
            pw,
            persisted.Email,
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism);

        try
        {
            return _crypto.ComputeMasterPasswordHash(mk, pw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mk);
        }
    }
}
