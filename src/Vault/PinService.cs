using System.Security.Cryptography;
using Core.Abstractions;
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
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        if (!_tokenStore.TryLoad(out var p))
            throw new InvalidOperationException("No saved session.");

        byte[] pinKey = _crypto.DeriveMasterKey(pin, p.Email, p.KdfType, p.KdfIterations, p.KdfMemory, p.KdfParallelism);
        try
        {
            var enc = _crypto.ProtectUserKey(pinKey, userKey);
            _tokenStore.Save(p with { PinProtectedUserKey = enc.ToString(), PinFailedAttempts = 0 });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinKey);
        }
    }

    public void ClearPin()
    {
        if (_tokenStore.TryLoad(out var p))
            _tokenStore.Save(p with { PinProtectedUserKey = null, PinFailedAttempts = 0 });
    }
}
