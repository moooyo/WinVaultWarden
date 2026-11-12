using System.Security.Cryptography;
using Core.Abstractions;
using Core.Services;
using Crypto;

namespace Vault;

// Windows Hello 解锁:用 Hello 签名派生的对称密钥包住 UserKey。结构镜像 PinService。
public sealed class HelloUnlockService : IHelloUnlockService
{
    private readonly CryptoService _crypto;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;

    public HelloUnlockService(CryptoService crypto, VaultSession session, ITokenStore tokenStore)
    {
        _crypto = crypto;
        _session = session;
        _tokenStore = tokenStore;
    }

    public bool IsHelloSet =>
        _tokenStore.TryLoad(out var p) && !string.IsNullOrEmpty(p.HelloProtectedUserKey);

    public void SetHello(byte[] signature, byte[] challenge)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(challenge);
        var lease = _session.CaptureLease();
        try
        {
            if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey) || copiedKey is null)
                throw new InvalidOperationException("Vault is locked.");
            using var userKey = copiedKey;
            if (!_tokenStore.TryLoad(out var persisted)
                || persisted.AccountId != lease.AccountId
                || persisted.Endpoints != lease.Endpoints
                || persisted.Generation != lease.Generation)
                throw new InvalidOperationException("No matching saved session.");

            LocalUnlockKeyProtection.RequireUserKeyBinding(userKey, persisted);

            using var helloKey = _crypto.DeriveKeyFromSecret(signature);
            var wrapped = LocalUnlockKeyProtection.Protect(_crypto, userKey, helloKey);
            if (!LocalUnlockKeyProtection.TryUpdate(_session, _tokenStore, lease, latest =>
            {
                LocalUnlockKeyProtection.RequireUnchangedKeyBinding(persisted, latest);
                return latest with
                {
                    HelloProtectedUserKey = wrapped,
                    HelloChallenge = Convert.ToBase64String(challenge),
                };
            }))
                throw new OperationCanceledException(lease.CancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public void ClearHello()
    {
        var lease = _session.CaptureLease();
        if (!_tokenStore.TryLoad(out var persisted)
            || persisted.AccountId != lease.AccountId
            || persisted.Endpoints != lease.Endpoints
            || persisted.Generation != lease.Generation)
            return;

        if (!LocalUnlockKeyProtection.TryUpdate(_session, _tokenStore, lease,
            latest => latest with { HelloProtectedUserKey = null, HelloChallenge = null }))
            throw new OperationCanceledException(lease.CancellationToken);
    }

    public byte[]? LoadChallenge()
    {
        if (_tokenStore.TryLoad(out var p) && !string.IsNullOrEmpty(p.HelloChallenge))
            return Convert.FromBase64String(p.HelloChallenge);
        return null;
    }
}
