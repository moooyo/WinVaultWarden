using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

public sealed class EmergencyAccessService : IEmergencyAccessService
{
    private readonly IVaultApiClientProvider _clients;
    private readonly CryptoService _crypto;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;

    public EmergencyAccessService(IVaultApiClientProvider clients, CryptoService crypto, VaultDecryptor decryptor, VaultSession session)
    {
        _clients = clients;
        _crypto = crypto;
        _decryptor = decryptor;
        _session = session;
    }

    // ===== 授予方（Grantor） =====

    public async Task<IReadOnlyList<EmergencyContact>> GetTrustedAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        var res = await api.GetTrustedAsync(linked.Token);
        _session.ThrowIfExpired(lease);
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

    public async Task InviteAsync(string email, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
    {
        ValidateWaitTimeDays(waitTimeDays);
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.InviteAsync(new EmergencyAccessInviteRequest { Email = email, Type = (int)type, WaitTimeDays = waitTimeDays }, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task ReinviteAsync(string id, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.ReinviteAsync(id, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task ConfirmAsync(string id, string granteeId, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        if (!_session.IsUnlocked(lease))
            throw new InvalidOperationException("Vault is locked.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        _session.ThrowIfExpired(lease);
        var pk = await api.GetPublicKeyAsync(granteeId, linked.Token);
        _session.ThrowIfExpired(lease);
        if (string.IsNullOrEmpty(pk.PublicKey))
            throw new EmergencyAccessOperationException("Grantee has no public key.");
        byte[] pubDer = Convert.FromBase64String(pk.PublicKey);
        if (!_session.TryCopyUserKey(lease, out var copiedKey) || copiedKey is null)
            throw new OperationCanceledException("Vault session lease expired.", lease.CancellationToken);
        string encryptedUserKey;
        using (copiedKey)
            encryptedUserKey = _crypto.EncryptRsa(copiedKey.FullKey, pubDer).ToString();

        _session.ThrowIfExpired(lease);
        await api.ConfirmAsync(id, new EmergencyAccessConfirmRequest { Key = encryptedUserKey }, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task UpdateAsync(string id, EmergencyAccessType type, int waitTimeDays, CancellationToken ct = default)
    {
        ValidateWaitTimeDays(waitTimeDays);
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.UpdateAsync(id, new EmergencyAccessUpdateRequest { Type = (int)type, WaitTimeDays = waitTimeDays }, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.DeleteAsync(id, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task ApproveAsync(string id, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.ApproveAsync(id, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task RejectAsync(string id, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.RejectAsync(id, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    // ===== 受托方（Grantee）— Task 6 =====

    public async Task<IReadOnlyList<GrantedAccess>> GetGrantedAsync(CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        var res = await api.GetGrantedAsync(linked.Token);
        _session.ThrowIfExpired(lease);
        return (res.Data ?? Array.Empty<EmergencyAccessGrantorDetailsDto>())
            .Select(d => new GrantedAccess(d.Id, d.GrantorId, d.Email, d.Name,
                (EmergencyAccessStatus)d.Status, (EmergencyAccessType)d.Type, d.WaitTimeDays))
            .ToArray();
    }

    public async Task AcceptAsync(string id, string token, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.AcceptAsync(id, new EmergencyAccessAcceptRequest { Token = token }, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task InitiateAsync(string id, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        await api.InitiateAsync(id, linked.Token);
        _session.ThrowIfExpired(lease);
    }

    public async Task<RecoveredVault> ViewAsync(string id, string grantorEmail, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        var res = await api.ViewAsync(id, linked.Token);
        _session.ThrowIfExpired(lease);
        using var grantorKey = RecoverGrantorKey(lease, res.KeyEncrypted);
        var ciphers = (res.Ciphers ?? Array.Empty<CipherDto>())
            .Select(dto => _decryptor.DecryptCipher(dto, grantorKey))
            .ToArray();
        _session.ThrowIfExpired(lease);
        return new RecoveredVault(grantorEmail, ciphers);
    }

    public async Task TakeoverAndResetPasswordAsync(string id, string grantorEmail, string newPassword, CancellationToken ct = default)
    {
        var lease = _session.CaptureLease();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var api = _clients.GetAuthenticated<IEmergencyAccessApiClient>(lease);
        var res = await api.TakeoverAsync(id, linked.Token);
        _session.ThrowIfExpired(lease);
        string salt;
        try
        {
            salt = MasterPasswordSaltPolicy.Resolve(grantorEmail, res.Salt);
        }
        catch (ArgumentException ex)
        {
            throw new EmergencyAccessOperationException(
                "The server returned an invalid master-password salt.",
                ex);
        }
        using var grantorKey = RecoverGrantorKey(lease, res.KeyEncrypted);
        byte[]? newMasterKey = null;
        try
        {
            try
            {
                newMasterKey = _crypto.DeriveMasterKey(newPassword, salt,
                    (Core.Enums.KdfType)res.Kdf, res.KdfIterations, res.KdfMemory, res.KdfParallelism);
            }
            catch (CryptographicException ex)
            {
                throw new EmergencyAccessOperationException(
                    "The grantor account uses KDF settings below this client's security floor.",
                    ex);
            }
            var newHash = _crypto.ComputeMasterPasswordHash(newMasterKey, newPassword);
            var protectedKey = _crypto.ProtectUserKey(newMasterKey, grantorKey);   // EncString
            _session.ThrowIfExpired(lease);
            await api.PasswordAsync(id, new EmergencyAccessPasswordRequest { NewMasterPasswordHash = newHash, Key = protectedKey.ToString() }, linked.Token);
            _session.ThrowIfExpired(lease);
        }
        finally
        {
            if (newMasterKey is not null) CryptographicOperations.ZeroMemory(newMasterKey);
        }
    }

    private static void ValidateWaitTimeDays(int waitTimeDays)
    {
        if (waitTimeDays is < 1 or > short.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(waitTimeDays),
                waitTimeDays,
                $"Wait time must be between 1 and {short.MaxValue} days.");
    }

    // 用受托方私钥 RSA-解出授予方 userKey（64 字节 enc+mac）
    private SymmetricCryptoKey RecoverGrantorKey(
        Core.Session.VaultSessionLease lease,
        string? keyEncrypted)
    {
        if (!_session.TryCopyUserKey(lease, out var copiedKey) || copiedKey is null)
            throw new EmergencyAccessOperationException("Vault is locked.");
        using var userKey = copiedKey;
        if (!_session.TryGetEncryptedPrivateKey(lease, out var encryptedPrivateKey))
            throw new OperationCanceledException("Vault session lease expired.", lease.CancellationToken);
        if (string.IsNullOrEmpty(encryptedPrivateKey))
            throw new EmergencyAccessOperationException("Private key unavailable; re-sync required.");
        if (string.IsNullOrEmpty(keyEncrypted))
            throw new EmergencyAccessOperationException("Server returned no encrypted key.");
        byte[] pkcs8 = _crypto.Decrypt(EncString.Parse(encryptedPrivateKey), userKey);
        byte[]? grantorKeyBytes = null;
        try
        {
            grantorKeyBytes = _crypto.DecryptRsa(EncString.Parse(keyEncrypted), pkcs8);
            _session.ThrowIfExpired(lease);
            var grantorKey = new SymmetricCryptoKey(grantorKeyBytes);
            grantorKeyBytes = null;
            return grantorKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            if (grantorKeyBytes is not null)
                CryptographicOperations.ZeroMemory(grantorKeyBytes);
        }
    }
}
