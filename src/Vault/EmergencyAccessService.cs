using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

public sealed class EmergencyAccessService : IEmergencyAccessService
{
    private readonly IEmergencyAccessApiClient _api;
    private readonly CryptoService _crypto;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;

    public EmergencyAccessService(IEmergencyAccessApiClient api, CryptoService crypto, VaultDecryptor decryptor, VaultSession session)
    {
        _api = api;
        _crypto = crypto;
        _decryptor = decryptor;
        _session = session;
    }

    // ===== 授予方（Grantor） =====

    public async Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default)
    {
        var res = await _api.GetTrustedAsync(ct);
        return (res.Data ?? Array.Empty<EmergencyAccessGranteeDetailsDto>())
            .Select(d => new EmergencyContact(
                d.Id,
                d.GranteeId,
                d.Email,
                d.Name,
                (EmergencyAccessStatus)d.Status,
                (EmergencyAccessType)d.Type,
                d.WaitTimeDays))
            .ToArray();
    }

    public Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
        => _api.InviteAsync(new EmergencyAccessInviteRequest { Email = email, Type = (int)type, WaitTimeDays = waitTimeDays }, ct);

    public Task ReinviteAsync(string id, CancellationToken ct = default)
        => _api.ReinviteAsync(id, ct);

    public async Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default)
    {
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        var pk = await _api.GetPublicKeyAsync(granteeId, ct);
        if (string.IsNullOrEmpty(pk.PublicKey))
            throw new EmergencyAccessOperationException("Grantee has no public key.");
        byte[] pubDer = Convert.FromBase64String(pk.PublicKey);
        var enc = _crypto.EncryptRsa(userKey.FullKey, pubDer);
        await _api.ConfirmAsync(id, new EmergencyAccessConfirmRequest { Key = enc.ToString() }, ct);
    }

    public Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
        => _api.UpdateAsync(id, new EmergencyAccessUpdateRequest { Type = (int)type, WaitTimeDays = waitTimeDays }, ct);

    public Task RemoveAsync(string id, CancellationToken ct = default)
        => _api.DeleteAsync(id, ct);

    public Task ApproveAsync(string id, CancellationToken ct = default)
        => _api.ApproveAsync(id, ct);

    public Task RejectAsync(string id, CancellationToken ct = default)
        => _api.RejectAsync(id, ct);

    // ===== 受托方（Grantee）— Task 6 =====

    // Task 6
    public Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    // Task 6
    public Task AcceptAsync(string id, string token, CancellationToken ct = default)
        => throw new NotImplementedException();

    // Task 6
    public Task InitiateAsync(string id, CancellationToken ct = default)
        => throw new NotImplementedException();

    // Task 6
    public Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default)
        => throw new NotImplementedException();

    // Task 6
    public Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default)
        => throw new NotImplementedException();
}
