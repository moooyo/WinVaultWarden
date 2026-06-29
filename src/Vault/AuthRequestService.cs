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
    private readonly IAuthRequestApiClient _api;
    private readonly CryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;

    public AuthRequestService(
        IAuthRequestApiClient api,
        CryptoService crypto,
        VaultSession session,
        ITokenStore tokenStore)
    {
        _api = api;
        _crypto = crypto;
        _session = session;
        _tokenStore = tokenStore;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingAuthRequest>> ListPendingAsync(CancellationToken ct = default)
    {
        var resp = await _api.GetPendingAsync(ct);
        return resp.Data
            .Select(r => new PendingAuthRequest(
                Id: r.Id,
                DeviceTypeName: r.RequestDeviceType,
                IpAddress: r.RequestIpAddress,
                CreationDate: r.CreationDate,
                PublicKey: r.PublicKey))
            .ToList()
            .AsReadOnly();
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

        await _api.ApproveAsync(
            id,
            new AuthResponseRequest(
                DeviceIdentifier: persisted.DeviceIdentifier,
                Key: encKey,
                MasterPasswordHash: null,
                RequestApproved: true),
            ct);
    }

    /// <inheritdoc />
    public async Task DenyAsync(string id, CancellationToken ct = default)
    {
        if (!_tokenStore.TryLoad(out var persisted))
            throw new AuthRequestOperationException("无法读取会话信息，请重新登录。");

        await _api.ApproveAsync(
            id,
            new AuthResponseRequest(
                DeviceIdentifier: persisted.DeviceIdentifier,
                Key: "",
                MasterPasswordHash: null,
                RequestApproved: false),
            ct);
    }
}
