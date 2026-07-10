using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// PIN/Hello 写入门禁:操作开始捕获的 lease 在 tokenStore.TryLoad 触发的回调中被 Lock() 使失效后,
// 不得重建/持久化任何凭据,必须以 OperationCanceledException 失败退出,且签名等敏感临时物料仍须清零。
public class CredentialPersistenceGateTests
{
    private sealed class CallbackTokenStore(
        PersistedSession persisted) : ITokenStore
    {
        private PersistedSession? _persisted = persisted;
        public Action? AfterLoad { get; init; }
        public int SaveCount { get; private set; }

        public bool TryLoad(out PersistedSession session)
        {
            session = _persisted!;
            var found = _persisted is not null;
            if (found)
                AfterLoad?.Invoke();
            return found;
        }

        public void Save(PersistedSession session)
        {
            _persisted = session;
            SaveCount++;
        }

        public void Clear() => _persisted = null;
    }

    private static VaultSession NewUnlockedSession(
        out VaultSessionLease lease,
        out PersistedSession persisted)
    {
        var session = new VaultSession();
        lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        using var sourceKey = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var sessionKey = new SymmetricCryptoKey(sourceKey.FullKey.ToArray());
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(sessionKey);
            session.SetTokens("access-a", "refresh-a");
        });
        persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Origin,
            lease.Generation,
            "a@example.com",
            "device-a",
            "refresh-a",
            "2.protected",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        return session;
    }

    [Fact]
    public void SetPin_StaleLease_DoesNotRecreateCredentials()
    {
        var session = NewUnlockedSession(out var lease, out var persisted);
        var store = new CallbackTokenStore(persisted)
        {
            AfterLoad = () => session.Lock(),
        };
        var service = new PinService(new CryptoService(), session, store);

        Assert.Throws<OperationCanceledException>(() => service.SetPin("123456"));

        Assert.Equal(0, store.SaveCount);
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public void SetHello_StaleLease_DoesNotPersistWrappedKey()
    {
        var session = NewUnlockedSession(out _, out var persisted);
        var store = new CallbackTokenStore(persisted)
        {
            AfterLoad = () => session.Lock(),
        };
        var service = new HelloUnlockService(new CryptoService(), session, store);
        var signature = Enumerable.Repeat((byte)7, 256).ToArray();

        Assert.Throws<OperationCanceledException>(() =>
            service.SetHello(signature, new byte[32]));

        Assert.Equal(0, store.SaveCount);
        Assert.All(signature, value => Assert.Equal(0, value));
    }
}
