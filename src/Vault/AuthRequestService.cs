using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

// 设备登录授权请求（auth-request）编排服务。
// 对应 Vaultwarden src/api/core/accounts.rs auth-requests 端点。
//
// ListPending  → GET  /api/auth-requests/pending，映射到 PendingAuthRequest 模型。
// ApproveAsync → 用发起方公钥 RSA-OAEP-SHA1 加密当前会话 UserKey，PUT 批准。
// DenyAsync    → PUT 拒绝，Key="" RequestApproved=false。
public sealed class AuthRequestService : IAuthRequestService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly CryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly FingerprintService _fingerprint;

    public AuthRequestService(
        IVaultApiClientProvider clients,
        CryptoService crypto,
        VaultSession session,
        ITokenStore tokenStore,
        FingerprintService fingerprint)
    {
        _clients = clients;
        _crypto = crypto;
        _session = session;
        _tokenStore = tokenStore;
        _fingerprint = fingerprint;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingAuthRequest>> ListPendingAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAuthRequestApiClient>(lease);
        var resp = await api.GetPendingAsync(linked.Token);
        _session.ThrowIfExpired(lease);

        // D1: 为每个请求派生指纹短语(账户邮箱 + 请求方公钥),供审批前人工核对。
        // 邮箱取自持久化会话 = 登录邮箱,与请求设备使用的 loginEmail 一致,使两端短语相同。
        var email = _tokenStore.TryLoad(out var persisted) ? persisted.Email : string.Empty;

        return resp.Data
            .Select(r => new PendingAuthRequest(
                Id: r.Id,
                DeviceTypeName: r.RequestDeviceType,
                IpAddress: r.RequestIpAddress,
                CreationDate: r.CreationDate,
                PublicKey: r.PublicKey)
            {
                FingerprintPhrase = DeriveFingerprint(r.PublicKey, email),
            })
            .ToList()
            .AsReadOnly();
    }

    // 尽力派生;邮箱缺失或公钥非法 base64 时返回空串(UI 显示为空 → 阻止盲目批准)。
    private string DeriveFingerprint(string publicKeyBase64, string email)
    {
        if (string.IsNullOrEmpty(email)) return string.Empty;
        try
        {
            return _fingerprint.GetFingerprintPhrase(Convert.FromBase64String(publicKeyBase64), email);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task ApproveAsync(string id, string publicKey, CancellationToken ct = default)
    {
        // 必须已解锁密码库
        var userKey = _session.UserKey
            ?? throw new AuthRequestOperationException("请先解锁密码库。");

        // 加载持久化会话以取得当前设备标识符
        if (!_tokenStore.TryLoad(out var persisted))
            throw new AuthRequestOperationException("无法读取会话信息，请重新登录。");

        // 用发起方公钥（SPKI DER）加密完整 UserKey（64 字节），得到 encType=4 的 EncString
        var publicKeyDer = Convert.FromBase64String(publicKey);
        var encKey = _crypto.EncryptRsa(userKey.FullKey, publicKeyDer).ToString();

        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAuthRequestApiClient>(lease);
        await api.ApproveAsync(
            id,
            new AuthResponseRequest(
                DeviceIdentifier: persisted.DeviceIdentifier,
                Key: encKey,
                MasterPasswordHash: null,
                RequestApproved: true),
            linked.Token);
        _session.ThrowIfExpired(lease);
    }

    /// <inheritdoc />
    public async Task DenyAsync(string id, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted))
            throw new AuthRequestOperationException("无法读取会话信息，请重新登录。");

        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAuthRequestApiClient>(lease);
        await api.ApproveAsync(
            id,
            new AuthResponseRequest(
                DeviceIdentifier: persisted.DeviceIdentifier,
                Key: "",
                MasterPasswordHash: null,
                RequestApproved: false),
            linked.Token);
        _session.ThrowIfExpired(lease);
    }
}
