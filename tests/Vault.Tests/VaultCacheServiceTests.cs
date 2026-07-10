using Api.Dtos;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultCacheServiceTests
{
    private static readonly SymmetricCryptoKey UserKey =
        new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
    private const string AccountId = "u";
    private const string Email = "me@example.com";
    private const string Server = "https://vault.example";

    // 用真实 Crypto 造一条加密态 cipher(名字 "Cached")的 SyncResponse。profileId 缺省与租约 AccountId 对齐。
    private static SyncResponse BuildSync(CryptoService crypto, string profileId = AccountId)
    {
        var name = crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("Cached"), UserKey).ToString();
        var dto = new CipherDto(
            Id: "c1", Type: 1, Name: name, Notes: null, Key: null, OrganizationId: null, FolderId: null,
            Favorite: false, Reprompt: 0, Login: null, Card: null, Identity: null, SecureNote: null,
            SshKey: null, Fields: null, CreationDate: null, RevisionDate: null, DeletedDate: null,
            Attachments: null, PasswordHistory: null);
        return new SyncResponse("sync", new ProfileDto(profileId, Email, "Me", null, null), [], [dto]);
    }

    private static (VaultCacheService svc, VaultSession session, FakeCacheStore store) New()
    {
        var crypto = new CryptoService();
        var store = new FakeCacheStore();
        var session = new VaultSession();
        return (new VaultCacheService(store, new VaultDecryptor(crypto), session), session, store);
    }

    private static VaultSessionLease Unlock(
        VaultSession session, string accountId = AccountId, string server = Server)
    {
        var lease = session.BeginAccountSession(accountId, Email, new ServerOrigin(server));
        session.SetUnlockedKey(UserKey);
        return lease;
    }

    [Fact]
    public void SaveThenTryLoadInto_PopulatesSnapshot_WithDataStaleTrue()
    {
        var (svc, session, _) = New();
        var lease = Unlock(session);

        svc.Save(lease, BuildSync(new CryptoService()), DateTimeOffset.UnixEpoch);
        var ok = svc.TryLoadInto(lease, UserKey);

        Assert.True(ok);
        var c = Assert.Single(session.Ciphers);
        Assert.Equal("Cached", c.Name);
        Assert.True(session.DataStale);
    }

    [Fact]
    public void TryLoadInto_AccountMismatch_ReturnsFalse()
    {
        var (_, seedSession, store) = New();
        var seedLease = Unlock(seedSession);
        new VaultCacheService(store, new VaultDecryptor(new CryptoService()), seedSession)
            .Save(seedLease, BuildSync(new CryptoService()), DateTimeOffset.UnixEpoch);

        var loadSession = new VaultSession();
        var loadSvc = new VaultCacheService(store, new VaultDecryptor(new CryptoService()), loadSession);
        var loadLease = loadSession.BeginAccountSession("other", Email, new ServerOrigin(Server));
        loadSession.SetUnlockedKey(UserKey);

        Assert.False(loadSvc.TryLoadInto(loadLease, UserKey));
    }

    [Fact]
    public void TryLoadInto_OriginMismatch_ReturnsFalse()
    {
        var (_, seedSession, store) = New();
        var seedLease = Unlock(seedSession);
        new VaultCacheService(store, new VaultDecryptor(new CryptoService()), seedSession)
            .Save(seedLease, BuildSync(new CryptoService()), DateTimeOffset.UnixEpoch);

        var loadSession = new VaultSession();
        var loadSvc = new VaultCacheService(store, new VaultDecryptor(new CryptoService()), loadSession);
        var loadLease = loadSession.BeginAccountSession(AccountId, Email, new ServerOrigin("https://other.example"));
        loadSession.SetUnlockedKey(UserKey);

        Assert.False(loadSvc.TryLoadInto(loadLease, UserKey));
    }

    [Fact]
    public void TryLoadInto_NoCache_ReturnsFalse()
    {
        var (svc, session, _) = New();
        var lease = Unlock(session);
        Assert.False(svc.TryLoadInto(lease, UserKey));
    }

    [Fact]
    public void TryLoadInto_SchemaVersionMismatch_ReturnsFalse()
    {
        // 未来版本 schema → 忽略缓存,走联网(D5)。
        var (svc, session, store) = New();
        var lease = Unlock(session);
        store.SaveVaultCache(new VaultCacheEntry(
            999, AccountId, new ServerOrigin(Server), lease.Generation, Email, DateTimeOffset.UnixEpoch, "{}"));
        Assert.False(svc.TryLoadInto(lease, UserKey));
    }

    [Fact]
    public void TryLoadInto_CorruptJson_ReturnsFalse()
    {
        // schema/account/origin/generation 都对,但 payload 损坏 → 反序列化失败,回退 false 而非外抛。
        var (svc, session, store) = New();
        var lease = Unlock(session);
        store.SaveVaultCache(new VaultCacheEntry(
            VaultCacheService.SchemaVersion, AccountId, new ServerOrigin(Server), lease.Generation,
            Email, DateTimeOffset.UnixEpoch, "{not valid json"));
        Assert.False(svc.TryLoadInto(lease, UserKey));
    }

    [Fact]
    public void TryLoadInto_ProfileIdNotMatchingAccount_ReturnsFalse()
    {
        // 头部账户对齐,但载荷 profile.Id 与租约账户不符 → 拒绝(防止串账户明文回灌)。
        var (svc, session, _) = New();
        var lease = Unlock(session);
        svc.Save(lease, BuildSync(new CryptoService(), profileId: "someone-else"), DateTimeOffset.UnixEpoch);
        Assert.False(svc.TryLoadInto(lease, UserKey));
    }

    [Fact]
    public void RebindGeneration_ThenLoad_SucceedsAfterGenerationRotation()
    {
        // 缓存以旧 generation 存;会话 generation 轮换后,RebindGeneration 重绑到当前租约再命中。
        var (_, seedSession, store) = New();
        var seedLease = Unlock(seedSession);
        new VaultCacheService(store, new VaultDecryptor(new CryptoService()), seedSession)
            .Save(seedLease, BuildSync(new CryptoService()), DateTimeOffset.UnixEpoch);

        var session = new VaultSession();
        var svc = new VaultCacheService(store, new VaultDecryptor(new CryptoService()), session);
        session.BeginAccountSession(AccountId, Email, new ServerOrigin(Server));
        var lease = session.BeginAccountSession(AccountId, Email, new ServerOrigin(Server));
        session.SetUnlockedKey(UserKey);

        Assert.NotEqual(seedLease.Generation, lease.Generation);
        Assert.False(svc.TryLoadInto(lease, UserKey)); // generation 不符,先拒绝
        Assert.True(svc.RebindGeneration(lease));
        Assert.True(svc.TryLoadInto(lease, UserKey));
        Assert.Equal("Cached", Assert.Single(session.Ciphers).Name);
    }
}
