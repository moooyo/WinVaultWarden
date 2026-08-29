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
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;

    public TwoFactorService(
        CryptoService crypto,
        IVaultApiClientProvider clients,
        VaultSession session,
        ITokenStore tokenStore)
    {
        _crypto = crypto;
        _clients = clients;
        _session = session;
        _tokenStore = tokenStore;
    }

    // -----------------------------------------------------------------------
    // ListProvidersAsync — 不需要主密码，直接拉取提供者列表
    // -----------------------------------------------------------------------

    public async Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        var response = await api.GetProvidersAsync(linked.Token);
        _session.ThrowIfExpired(lease);
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
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var response = await api.GetAuthenticatorAsync(new PasswordVerifyRequest(mphash), linked.Token);
        _session.ThrowIfExpired(lease);

        var secret = response.Key;
        var otpauth = $"otpauth://totp/{Uri.EscapeDataString(persisted.Email)}?secret={Uri.EscapeDataString(secret)}&issuer=WinVaultWarden";
        return (secret, otpauth);
    }

    // -----------------------------------------------------------------------
    // EnableTotpAsync — 把 secret(=key) + code(=token) + hash 发给服务端启用，再拉 recovery code
    // -----------------------------------------------------------------------

    public async Task<TotpActivationResult> EnableTotpAsync(
        string pw, string secret, string code, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var enabled = await api.EnableAuthenticatorAsync(
            new EnableAuthenticatorRequest(Key: secret, Token: code, MasterPasswordHash: mphash), linked.Token);
        if (!enabled.Enabled)
            throw new TwoFactorOperationException("服务器未确认 TOTP 已启用，请稍后重试。");

        try
        {
            _session.ThrowIfExpired(lease);
            var recover = await api.GetRecoverAsync(new PasswordVerifyRequest(mphash), linked.Token);
            _session.ThrowIfExpired(lease);
            return string.IsNullOrWhiteSpace(recover.Code)
                ? TotpActivationResult.EnabledWithoutRecoveryCode()
                : TotpActivationResult.EnabledWithRecoveryCode(recover.Code);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Enabling TOTP is already committed. Recovery-code retrieval is a
            // separate, retryable read and must not make activation look failed.
            return TotpActivationResult.EnabledWithoutRecoveryCode();
        }
    }

    public async Task<string> GetRecoveryCodeAsync(string pw, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var recover = await api.GetRecoverAsync(new PasswordVerifyRequest(mphash), linked.Token);
        _session.ThrowIfExpired(lease);
        return recover.Code ?? string.Empty;
    }

    // -----------------------------------------------------------------------
    // BeginEmailSetupAsync — 拿到服务端返回的目标邮箱（脱敏版本）
    // -----------------------------------------------------------------------

    public async Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var response = await api.GetEmailAsync(new PasswordVerifyRequest(mphash), linked.Token);
        _session.ThrowIfExpired(lease);
        return response.Email;
    }

    // -----------------------------------------------------------------------
    // SendEmailAsync — 触发服务端向指定邮箱发送验证码
    // -----------------------------------------------------------------------

    public async Task SendEmailAsync(string pw, string email, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.SendEmailAsync(new SendEmailRequest(Email: email, MasterPasswordHash: mphash), linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // -----------------------------------------------------------------------
    // EnableEmailAsync — 提交邮箱 token 完成 Email 两步验证启用
    // -----------------------------------------------------------------------

    public async Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.EnableEmailAsync(
            new EmailVerifyRequest(Email: email, Token: token, MasterPasswordHash: mphash), linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // -----------------------------------------------------------------------
    // DisableAsync — 禁用指定类型的两步验证提供者
    // -----------------------------------------------------------------------

    public async Task DisableAsync(string pw, int type, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        var mphash = ComputeHash(pw, persisted);
        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<ITwoFactorApiClient>(lease);
        _session.ThrowIfExpired(lease);
        await api.DisableAsync(new DisableTwoFactorRequest(MasterPasswordHash: mphash, Type: type), linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // -----------------------------------------------------------------------
    // 私有辅助
    // -----------------------------------------------------------------------

    /// <summary>
    /// 加载持久化会话，失败则抛 <see cref="TwoFactorOperationException"/>。
    /// </summary>
    private PersistedSession LoadPersisted(Core.Session.VaultSessionLease lease)
    {
        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted))
            throw new TwoFactorOperationException("持久化会话与当前账户不匹配，请重新登录后重试。");
        return persisted;
    }

    /// <summary>
    /// 从持久化会话派生 masterKey，计算 masterPasswordHash，然后无条件清零 masterKey。
    /// </summary>
    private string ComputeHash(string pw, PersistedSession persisted)
    {
        var mk = _crypto.DeriveMasterKey(
            pw,
            persisted.EffectiveMasterPasswordSalt,
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
