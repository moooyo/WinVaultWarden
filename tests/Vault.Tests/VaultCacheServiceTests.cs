using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using Core.Models;
using Core.Session;
using Crypto;
using Crypto.V2;
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
        return new SyncResponse(
            "sync",
            new ProfileDto(profileId, Email, "Me", null, null)
            {
                SecurityStamp = "stamp-1",
            },
            [],
            [dto]);
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
    public void SaveThenTryLoadInto_RestoresSyncSendsDomainsAndEffectivePolicies()
    {
        var crypto = new CryptoService();
        var sendCrypto = new SendCryptoService(crypto);
        var seed = sendCrypto.GenerateSeed();
        using var sendKey = sendCrypto.DeriveCryptoKey(seed);
        var send = new SendResponseDto(
            "send-1",
            "access-1",
            0,
            sendCrypto.EncryptField("Cached Send", sendKey),
            null,
            new SendTextDto(sendCrypto.EncryptField("cached secret", sendKey), false),
            null,
            sendCrypto.WrapSeed(seed, UserKey),
            null,
            0,
            null,
            (int)Core.Enums.SendAuthType.None,
            false,
            false,
            DateTimeOffset.UnixEpoch,
            null,
            DateTimeOffset.UnixEpoch.AddDays(1),
            "send");
        var sync = BuildSync(crypto) with
        {
            Sends = [send],
            Domains = new DomainsDto([["example.com", "example.net"]], [], "domains"),
            PoliciesNew =
            [
                new OrganizationPolicyDto(
                    "policy-1",
                    "org-1",
                    (int)Core.Enums.OrganizationPolicyType.DisableSend,
                    true,
                    default),
            ],
            Profile = BuildSync(crypto).Profile! with
            {
                OrganizationsNew =
                [
                    new ProfileOrganizationDto("org-1", "Team", null, 1, 2),
                ],
            },
        };
        var (service, session, _) = New();
        var lease = Unlock(session);

        Assert.True(service.Save(lease, sync, DateTimeOffset.UnixEpoch));
        Assert.True(service.TryLoadInto(lease, UserKey));

        Assert.True(session.DataStale);
        Assert.Equal("Cached Send", Assert.Single(session.Sends).Name);
        Assert.Equal("cached secret", Assert.Single(session.Sends).Text!.Content);
        Assert.Equal("example.net", session.Domains!.EquivalentDomains[0][1]);
        Assert.Equal(1, Assert.Single(session.PolicyOrganizations).Status);
        Assert.Equal("policy-1", Assert.Single(session.Policies).Id);
    }

    [Fact]
    public void SaveThenTryLoadInto_RedactsSecurityStampAndDoesNotRestoreBaseline()
    {
        var (service, session, store) = New();
        var lease = Unlock(session);
        var sync = BuildSync(new CryptoService());

        service.Save(lease, sync, DateTimeOffset.UnixEpoch);

        var persisted = JsonSerializer.Deserialize(
            store.LastSaved!.SyncResponseJson,
            ApiJsonContext.Default.SyncResponse);
        Assert.Equal("stamp-1", sync.Profile!.SecurityStamp);
        Assert.Null(persisted!.Profile!.SecurityStamp);
        Assert.True(service.TryLoadInto(lease, UserKey));
        Assert.Equal(
            SecurityStampComparison.NoBaseline,
            session.CompareSecurityStamp(lease, "stamp-2"));
    }

    [Fact]
    public void SaveThenTryLoadInto_AccountKeysOnly_CommitsCanonicalWrappedPrivateKey()
    {
        var crypto = new CryptoService();
        var wrappedPrivateKey = crypto.Encrypt([1, 2, 3, 4], UserKey).ToString();
        using var accountKeys = JsonDocument.Parse($$"""
        {
          "publicKeyEncryptionKeyPair": {
            "wrappedPrivateKey": {{JsonSerializer.Serialize(wrappedPrivateKey)}},
            "publicKey": "v1-public-key"
          },
          "signatureKeyPair": null,
          "securityState": null
        }
        """);
        var sync = BuildSync(crypto) with
        {
            Profile = BuildSync(crypto).Profile! with
            {
                PrivateKey = null,
                AccountKeys = accountKeys.RootElement.Clone(),
            },
        };
        var (service, session, _) = New();
        var lease = Unlock(session);

        service.Save(lease, sync, DateTimeOffset.UnixEpoch);
        var loaded = service.TryLoadInto(lease, UserKey);

        Assert.True(loaded);
        Assert.Equal(wrappedPrivateKey, session.EncryptedPrivateKey);
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
    public void TryLoadInto_WhenStoreReadThrowsIOException_ReturnsFalse()
    {
        var (svc, session, store) = New();
        var lease = Unlock(session);
        store.BeforeLoad = () => throw new IOException("read failed");

        Assert.False(svc.TryLoadInto(lease, UserKey));
    }

    [Fact]
    public void RebindGeneration_WhenStoreReadThrowsUnauthorizedAccess_ReturnsFalse()
    {
        var (svc, session, store) = New();
        var lease = Unlock(session);
        store.BeforeLoad = () => throw new UnauthorizedAccessException("read denied");

        Assert.False(svc.RebindGeneration(lease));
    }

    [Fact]
    public void TryLoadInto_WhenStoreReadThrowsUnexpectedException_Propagates()
    {
        var (svc, session, store) = New();
        var lease = Unlock(session);
        store.BeforeLoad = () => throw new InvalidOperationException("unexpected failure");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            svc.TryLoadInto(lease, UserKey));

        Assert.Equal("unexpected failure", exception.Message);
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
    public void TryLoadInto_SchemaTwo_MigratesEndpointsWithoutLosingOfflineCache()
    {
        var (svc, session, store) = New();
        var lease = Unlock(session);
        var sync = BuildSync(new CryptoService());
        var json = JsonSerializer.Serialize(sync, ApiJsonContext.Default.SyncResponse);
        store.SaveVaultCache(new VaultCacheEntry(
            VaultCacheEntry.LegacySchemaVersion,
            AccountId,
            new ServerOrigin(Server),
            lease.Generation,
            Email,
            DateTimeOffset.UnixEpoch,
            json));

        var loaded = svc.TryLoadInto(lease, UserKey);

        Assert.True(loaded);
        Assert.Equal(VaultCacheEntry.CurrentSchemaVersion, store.LastSaved!.SchemaVersion);
        Assert.Equal(lease.Endpoints, store.LastSaved.Endpoints);
        Assert.Equal("Cached", Assert.Single(session.Ciphers).Name);
    }

    [Fact]
    public void TryLoadInto_SchemaTwo_WhenMigrationSaveThrowsUnexpectedException_Propagates()
    {
        var (svc, session, store) = New();
        var lease = Unlock(session);
        var sync = BuildSync(new CryptoService());
        var json = JsonSerializer.Serialize(sync, ApiJsonContext.Default.SyncResponse);
        store.SaveVaultCache(new VaultCacheEntry(
            VaultCacheEntry.LegacySchemaVersion,
            AccountId,
            new ServerOrigin(Server),
            lease.Generation,
            Email,
            DateTimeOffset.UnixEpoch,
            json));
        store.BeforeSave = () => throw new InvalidOperationException("unexpected failure");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            svc.TryLoadInto(lease, UserKey));

        Assert.Equal("unexpected failure", exception.Message);
        Assert.Empty(session.Ciphers);
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
    public void TryLoadInto_V2InvalidAccountKeys_ReturnsFalse()
    {
        var crypto = new CryptoService();
        var store = new FakeCacheStore();
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            AccountId,
            Email,
            new ServerOrigin(Server));
        var sessionKey = V2UserKey.Generate();
        var transport = sessionKey.ExportTransport();
        V2UserKey loadKey;
        try
        {
            loadKey = V2UserKey.ImportTransport(transport);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
        session.SetUnlockedKey(sessionKey);
        using var invalidAccountKeys = JsonDocument.Parse("{}");
        var sync = new SyncResponse(
            "sync",
            new ProfileDto(AccountId, Email, "Me", null, null)
            {
                AccountKeys = invalidAccountKeys.RootElement.Clone(),
            });
        var service = new VaultCacheService(
            store,
            new VaultDecryptor(crypto),
            session);
        service.Save(lease, sync, DateTimeOffset.UnixEpoch);

        using (loadKey)
            Assert.False(service.TryLoadInto(lease, loadKey));
        session.Lock();
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

    [Fact]
    public void RebindGeneration_WhenSaveThrowsIOException_ReturnsFalse()
    {
        AssertRebindWriteFailureReturnsFalse(new IOException("disk full"));
    }

    [Fact]
    public void RebindGeneration_WhenSaveThrowsUnauthorizedAccess_ReturnsFalse()
    {
        AssertRebindWriteFailureReturnsFalse(
            new UnauthorizedAccessException("cache directory is read-only"));
    }

    [Fact]
    public void RebindGeneration_WhenSaveThrowsCryptographicException_ReturnsFalse()
    {
        AssertRebindWriteFailureReturnsFalse(
            new CryptographicException("DPAPI protect failed"));
    }

    [Fact]
    public void RebindGeneration_WhenSaveThrowsUnexpectedException_Propagates()
    {
        var (svc, session, store, lease, _) = NewRebindFixture();
        store.BeforeSave = () => throw new InvalidOperationException("unexpected failure");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            svc.RebindGeneration(lease));

        Assert.Equal("unexpected failure", exception.Message);
        Assert.True(session.IsUnlocked(lease));
    }

    private static void AssertRebindWriteFailureReturnsFalse(Exception writeFailure)
    {
        var (svc, session, store, lease, cachedGeneration) = NewRebindFixture();
        store.BeforeSave = () => throw writeFailure;

        var rebound = svc.RebindGeneration(lease);

        Assert.False(rebound);
        Assert.True(session.IsUnlocked(lease));
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(cachedGeneration, store.LastSaved!.Generation);
    }

    private static (
        VaultCacheService Service,
        VaultSession Session,
        FakeCacheStore Store,
        VaultSessionLease Lease,
        long CachedGeneration) NewRebindFixture()
    {
        var (_, seedSession, store) = New();
        var seedLease = Unlock(seedSession);
        new VaultCacheService(store, new VaultDecryptor(new CryptoService()), seedSession)
            .Save(seedLease, BuildSync(new CryptoService()), DateTimeOffset.UnixEpoch);

        var session = new VaultSession();
        var service = new VaultCacheService(
            store,
            new VaultDecryptor(new CryptoService()),
            session);
        session.BeginAccountSession(AccountId, Email, new ServerOrigin(Server));
        var lease = session.BeginAccountSession(AccountId, Email, new ServerOrigin(Server));
        session.SetUnlockedKey(UserKey);

        Assert.NotEqual(seedLease.Generation, lease.Generation);
        return (service, session, store, lease, seedLease.Generation);
    }
}
