using System.Security.Cryptography;
using Core.Abstractions;
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

    public void SetPin(string pin)
    {
        if (!PinPolicy.IsValid(pin))
            throw new ArgumentException($"PIN 至少需要 {PinPolicy.MinLength} 位。", nameof(pin));

        var lease = _session.CaptureLease();
        if (!_session.TryCopyUserKey(lease, out var copiedKey))
            throw new InvalidOperationException("Vault is locked.");
        using var userKey = copiedKey;
        if (!_tokenStore.TryLoad(out var persisted)
            || persisted.AccountId != lease.AccountId
            || persisted.Endpoints != lease.Endpoints
            || persisted.Generation != lease.Generation)
            throw new InvalidOperationException("No matching saved session.");

        var pinKey = _crypto.DeriveMasterKey(
            pin,
            MasterPasswordSaltPolicy.NormalizeEmail(persisted.Email),
            persisted.KdfType,
            persisted.KdfIterations,
            persisted.KdfMemory,
            persisted.KdfParallelism);
        try
        {
            var wrapped = _crypto.ProtectUserKey(pinKey, userKey!);
            if (!_session.TryCommit(lease, () =>
                _tokenStore.Save(persisted with
                {
                    PinProtectedUserKey = wrapped.ToString(),
                    PinFailedAttempts = 0,
                })))
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

        if (!_session.TryCommit(lease, () =>
            _tokenStore.Save(persisted with { PinProtectedUserKey = null, PinFailedAttempts = 0 })))
            throw new OperationCanceledException(lease.CancellationToken);
    }
}
