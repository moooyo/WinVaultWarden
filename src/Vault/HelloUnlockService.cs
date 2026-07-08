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
        var userKey = _session.UserKey ?? throw new InvalidOperationException("Vault is locked.");
        if (!_tokenStore.TryLoad(out var p))
            throw new InvalidOperationException("No saved session.");

        using var helloKey = _crypto.DeriveKeyFromSecret(signature);
        try
        {
            var enc = _crypto.Encrypt(userKey.FullKey, helloKey);
            _tokenStore.Save(p with
            {
                HelloProtectedUserKey = enc.ToString(),
                HelloChallenge = Convert.ToBase64String(challenge),
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    public void ClearHello()
    {
        if (_tokenStore.TryLoad(out var p))
            _tokenStore.Save(p with { HelloProtectedUserKey = null, HelloChallenge = null });
    }

    public byte[]? LoadChallenge()
    {
        if (_tokenStore.TryLoad(out var p) && !string.IsNullOrEmpty(p.HelloChallenge))
            return Convert.FromBase64String(p.HelloChallenge);
        return null;
    }
}
