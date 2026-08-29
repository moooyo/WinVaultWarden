using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Core.Session;
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
        var persisted = LoadPersisted(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAuthRequestApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var resp = await api.GetPendingAsync(linked.Token);
        _session.ThrowIfExpired(lease);

        // D1: 为每个请求派生指纹短语(账户邮箱 + 请求方公钥),供审批前人工核对。
        // 邮箱取自持久化会话 = 登录邮箱,与请求设备使用的 loginEmail 一致,使两端短语相同。
        var pending = resp.Data
            .Select(r => new PendingAuthRequest(
                Id: r.Id,
                DeviceTypeName: r.RequestDeviceType,
                IpAddress: r.RequestIpAddress,
                CreationDate: r.CreationDate,
                PublicKey: r.PublicKey)
            {
                FingerprintPhrase = DeriveFingerprint(r.PublicKey, persisted.Email),
            })
            .ToList()
            .AsReadOnly();
        _session.ThrowIfExpired(lease);
        return pending;
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
        var lease = CaptureLeaseForApproval();
        var persisted = LoadPersisted(lease);
        if (!_session.TryCopyUserKey(lease, out var copiedKey) || copiedKey is null)
            throw new AuthRequestOperationException("请先解锁密码库。");

        // 用发起方公钥（SPKI DER）加密完整 UserKey（64 字节），得到 encType=4 的 EncString
        string encKey;
        using (copiedKey)
        {
            var publicKeyDer = Convert.FromBase64String(publicKey);
            encKey = _crypto.EncryptRsa(copiedKey.FullKey, publicKeyDer).ToString();
        }

        _session.ThrowIfExpired(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAuthRequestApiClient>(lease);
        _session.ThrowIfExpired(lease);
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
        var lease = _session.CaptureLease();
        var persisted = LoadPersisted(lease);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IAuthRequestApiClient>(lease);
        _session.ThrowIfExpired(lease);
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

    private VaultSessionLease CaptureLeaseForApproval()
    {
        try
        {
            return _session.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            throw new AuthRequestOperationException("请先解锁密码库。");
        }
    }

    private PersistedSession LoadPersisted(VaultSessionLease lease)
    {
        if (!_tokenStore.TryLoad(out var persisted)
            || !_session.IsCurrent(lease, persisted))
            throw new AuthRequestOperationException("持久化会话与当前账户不匹配，请重新登录。");
        return persisted;
    }
}
