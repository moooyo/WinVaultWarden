using System.Security.Cryptography;
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

    public async Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default)
    {
        var res = await _api.GetGrantedAsync(ct);
        return (res.Data ?? Array.Empty<EmergencyAccessGrantorDetailsDto>())
            .Select(d => new GrantedAccess(d.Id, d.GrantorId, d.Email, d.Name,
                (EmergencyAccessStatus)d.Status, (EmergencyAccessType)d.Type, d.WaitTimeDays))
            .ToArray();
    }

    public Task AcceptAsync(string id, string token, CancellationToken ct = default)
        => _api.AcceptAsync(id, new EmergencyAccessAcceptRequest { Token = token }, ct);

    public Task InitiateAsync(string id, CancellationToken ct = default) => _api.InitiateAsync(id, ct);

    public async Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default)
    {
        var res = await _api.ViewAsync(id, ct);
        var grantorKey = RecoverGrantorKey(res.KeyEncrypted);
        try
        {
            var ciphers = (res.Ciphers ?? Array.Empty<CipherDto>())
                .Select(dto => _decryptor.DecryptCipher(dto, grantorKey))
                .ToArray();
            return new RecoveredVault(grantorEmail, ciphers);
        }
        finally
        {
            ZeroKey(grantorKey);
        }
    }

    public async Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default)
    {
        var res = await _api.TakeoverAsync(id, ct);
        var grantorKey = RecoverGrantorKey(res.KeyEncrypted);
        byte[]? newMasterKey = null;
        try
        {
            newMasterKey = _crypto.DeriveMasterKey(newPassword, grantorEmail,
                (Core.Enums.KdfType)res.Kdf, res.KdfIterations, res.KdfMemory, res.KdfParallelism);
            var newHash = _crypto.ComputeMasterPasswordHash(newMasterKey, newPassword);
            var protectedKey = _crypto.ProtectUserKey(newMasterKey, grantorKey);   // EncString
            await _api.PasswordAsync(id, new EmergencyAccessPasswordRequest { NewMasterPasswordHash = newHash, Key = protectedKey.ToString() }, ct);
        }
        finally
        {
            if (newMasterKey is not null) CryptographicOperations.ZeroMemory(newMasterKey);
            ZeroKey(grantorKey);
        }
    }

    private static void ZeroKey(SymmetricCryptoKey k)
    {
        CryptographicOperations.ZeroMemory(k.FullKey);
        CryptographicOperations.ZeroMemory(k.EncKey);
        if (k.MacKey is not null)
            CryptographicOperations.ZeroMemory(k.MacKey);
    }

    // 用受托方私钥 RSA-解出授予方 userKey（64 字节 enc+mac）
    private SymmetricCryptoKey RecoverGrantorKey(string? keyEncrypted)
    {
        var userKey = _session.UserKey ?? throw new EmergencyAccessOperationException("Vault is locked.");
        if (string.IsNullOrEmpty(_session.EncryptedPrivateKey))
            throw new EmergencyAccessOperationException("Private key unavailable; re-sync required.");
        if (string.IsNullOrEmpty(keyEncrypted))
            throw new EmergencyAccessOperationException("Server returned no encrypted key.");
        byte[] pkcs8 = _crypto.Decrypt(EncString.Parse(_session.EncryptedPrivateKey), userKey);
        try
        {
            byte[] grantorKeyBytes = _crypto.DecryptRsa(EncString.Parse(keyEncrypted), pkcs8);
            return new SymmetricCryptoKey(grantorKeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }
}
