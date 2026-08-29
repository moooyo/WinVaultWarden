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
            if (!_session.TryCopyUserKey(lease, out var copiedKey))
                throw new InvalidOperationException("Vault is locked.");
            using var userKey = copiedKey;
            if (!_tokenStore.TryLoad(out var persisted)
                || persisted.AccountId != lease.AccountId
                || persisted.Endpoints != lease.Endpoints
                || persisted.Generation != lease.Generation)
                throw new InvalidOperationException("No matching saved session.");

            using var helloKey = _crypto.DeriveKeyFromSecret(signature);
            var wrapped = _crypto.Encrypt(userKey!.FullKey, helloKey).ToString();
            if (!_session.TryCommit(lease, () =>
                _tokenStore.Save(persisted with
                {
                    HelloProtectedUserKey = wrapped,
                    HelloChallenge = Convert.ToBase64String(challenge),
                })))
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

        if (!_session.TryCommit(lease, () =>
            _tokenStore.Save(persisted with { HelloProtectedUserKey = null, HelloChallenge = null })))
            throw new OperationCanceledException(lease.CancellationToken);
    }

    public byte[]? LoadChallenge()
    {
        if (_tokenStore.TryLoad(out var p) && !string.IsNullOrEmpty(p.HelloChallenge))
            return Convert.FromBase64String(p.HelloChallenge);
        return null;
    }
}
