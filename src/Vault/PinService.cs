using System.Security.Cryptography;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

public sealed class PinService : IPinService
{
    private readonly CryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;

    public PinService(CryptoService crypto, VaultSession session, ITokenStore tokenStore)
    {
        _crypto = crypto;
        _session = session;
        _tokenStore = tokenStore;
    }

    public bool IsPinSet =>
        _tokenStore.TryLoad(out var p) && !string.IsNullOrEmpty(p.PinProtectedUserKey);

    public bool RequireMasterPasswordOnRestart =>
        _tokenStore.TryLoad(out var persisted) && persisted.RequireMasterPasswordOnRestart;

    public bool CanRequireMasterPasswordOnRestart =>
        _tokenStore.TryLoad(out var persisted) && HasMasterPasswordUnlock(persisted);

    public bool RequiresMasterPasswordBeforePin =>
        _tokenStore.TryLoad(out var persisted)
        && PinUnlockSessionPolicy.RequiresMasterPassword(_session, persisted);

    public void SetPin(string pin) => SetPinCore(pin, null);

    public void SetPin(string pin, bool requireMasterPasswordOnRestart) =>
        SetPinCore(pin, requireMasterPasswordOnRestart);

    private void SetPinCore(string pin, bool? requireMasterPasswordOnRestart)
    {
        VaultPolicyEvaluator.ThrowIfEnabled(
            _session,
            Core.Enums.OrganizationPolicyType.RemoveUnlockWithPin,
            "An organization policy prevents unlocking with a PIN.");
        if (!PinPolicy.IsValid(pin))
            throw new ArgumentException($"PIN 至少需要 {PinPolicy.MinLength} 位。", nameof(pin));

        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey) || copiedKey is null)
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey;
        if (!_tokenStore.TryLoad(out var persisted)
            || persisted.AccountId != lease.AccountId
            || persisted.Endpoints != lease.Endpoints
            || persisted.Generation != lease.Generation)
            throw new InvalidOperationException("No matching saved session.");

        LocalUnlockKeyProtection.RequireUserKeyBinding(userKey, persisted);
        if (requireMasterPasswordOnRestart == true && !HasMasterPasswordUnlock(persisted))
            throw new InvalidOperationException("This account has no master-password unlock method.");

        var pinKey = _crypto.DeriveMasterKey(
            pin,
            MasterPasswordSaltPolicy.NormalizeEmail(persisted.Email),
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism);
        try
        {
            using var stretchedKey = _crypto.StretchMasterKey(pinKey);
            var wrapped = LocalUnlockKeyProtection.Protect(_crypto, userKey, stretchedKey);
            if (!LocalUnlockKeyProtection.TryUpdate(_session, _tokenStore, lease, latest =>
            {
                LocalUnlockKeyProtection.RequireUnchangedKeyBinding(persisted, latest);
                var requireMasterPassword = requireMasterPasswordOnRestart
                    ?? latest.RequireMasterPasswordOnRestart;
                if (requireMasterPassword && !HasMasterPasswordUnlock(latest))
                    throw new InvalidOperationException("This account has no master-password unlock method.");
                return latest with
                {
                    PinProtectedUserKey = wrapped,
                    PinFailedAttempts = 0,
                    RequireMasterPasswordOnRestart = requireMasterPassword,
                };
            }, (before, after) => PinUnlockSessionPolicy.RebindAfterPinChange(_session, before, after)))
                throw new OperationCanceledException(lease.CancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinKey);
        }
    }

    public void ClearPin()
    {
        var lease = _session.CaptureLease();
        if (!_tokenStore.TryLoad(out var persisted)
            || persisted.AccountId != lease.AccountId
            || persisted.Endpoints != lease.Endpoints
            || persisted.Generation != lease.Generation)
            return;

        if (!LocalUnlockKeyProtection.TryUpdate(_session, _tokenStore, lease,
            latest => latest with { PinProtectedUserKey = null, PinFailedAttempts = 0 },
            (_, after) => PinUnlockSessionPolicy.Forget(_session, after)))
            throw new OperationCanceledException(lease.CancellationToken);
    }

    public void SetRequireMasterPasswordOnRestart(bool value)
    {
        var lease = _session.CaptureLease();
        if (!_session.IsUnlocked(lease))
            throw new InvalidOperationException("Unlock the vault before changing its PIN policy.");
        if (!LocalUnlockKeyProtection.TryUpdate(_session, _tokenStore, lease, latest =>
        {
            if (string.IsNullOrEmpty(latest.PinProtectedUserKey))
                throw new InvalidOperationException("Configure a PIN before changing its restart policy.");
            if (value && !HasMasterPasswordUnlock(latest))
                throw new InvalidOperationException("This account has no master-password unlock method.");
            return latest with { RequireMasterPasswordOnRestart = value };
        }))
            throw new OperationCanceledException(lease.CancellationToken);
    }

    private static bool HasMasterPasswordUnlock(PersistedSession persisted) =>
        persisted.UnlockMethod == PersistedSessionUnlockMethod.MasterPassword
        && persisted.HasMasterPassword != false
        && !string.IsNullOrEmpty(persisted.ProtectedUserKey);
}
