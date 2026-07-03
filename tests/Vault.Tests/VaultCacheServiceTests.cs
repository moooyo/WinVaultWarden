using Api.Dtos;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultCacheServiceTests
{
    private static readonly SymmetricCryptoKey UserKey =
        new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
    private const string Email = "me@example.com";
    private const string Server = "https://vault.example";

    // 用真实 Crypto 造一条加密态 cipher(名字 "Cached")的 SyncResponse。
    private static SyncResponse BuildSync(CryptoService crypto)
    {
        var name = crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes("Cached"), UserKey).ToString();
        var dto = new CipherDto(
            Id: "c1", Type: 1, Name: name, Notes: null, Key: null, OrganizationId: null, FolderId: null,
            Favorite: false, Reprompt: 0, Login: null, Card: null, Identity: null, SecureNote: null,
            SshKey: null, Fields: null, CreationDate: null, RevisionDate: null, DeletedDate: null,
            Attachments: null, PasswordHistory: null);
        return new SyncResponse("sync", new ProfileDto("u", Email, "Me", null, null), [], [dto]);
    }

    private static (VaultCacheService svc, VaultSession session, FakeCacheStore store) New()
    {
        var crypto = new CryptoService();
        var store = new FakeCacheStore();
        var session = new VaultSession();
        return (new VaultCacheService(store, new VaultDecryptor(crypto), session), session, store);
    }

    [Fact]
    public void SaveThenTryLoadInto_PopulatesSnapshot_WithDataStaleTrue()
    {
        var (svc, session, _) = New();
        session.SetUnlockedKey(UserKey);
        var sync = BuildSync(new CryptoService());

        svc.Save(sync, Email, Server, DateTimeOffset.UnixEpoch);

        // 模拟"新会话解锁":清掉快照,再从缓存载入。
        session.Lock();
        session.SetUnlockedKey(UserKey);
        var ok = svc.TryLoadInto(Email, Server, UserKey);

        Assert.True(ok);
        var c = Assert.Single(session.Ciphers);
        Assert.Equal("Cached", c.Name);
        Assert.True(session.DataStale);
    }

    [Theory]
    [InlineData("other@example.com", "https://vault.example")]
    [InlineData("me@example.com", "https://other.example")]
    public void TryLoadInto_EmailOrServerMismatch_ReturnsFalse(string email, string server)
    {
        var (svc, session, _) = New();
        session.SetUnlockedKey(UserKey);
        svc.Save(BuildSync(new CryptoService()), Email, Server, DateTimeOffset.UnixEpoch);
        session.Lock();
        session.SetUnlockedKey(UserKey);

        Assert.False(svc.TryLoadInto(email, server, UserKey));
    }

    [Fact]
    public void TryLoadInto_NoCache_ReturnsFalse()
    {
        var (svc, session, _) = New();
        session.SetUnlockedKey(UserKey);
        Assert.False(svc.TryLoadInto(Email, Server, UserKey));
    }
}
