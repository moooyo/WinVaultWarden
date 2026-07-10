using System.Net;
using Api;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SyncServiceTests
{
    private const string Server = "https://vault.example";

    [Fact]
    public async Task SyncAsync_OnSuccess_WritesCacheAndMarksSnapshotFresh()
    {
        var crypto = new CryptoService();
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        var api = new ApiClient(new HttpClient(handler), new ServerOrigin(Server));
        var session = new VaultSession();
        // profile.id 必须等于租约 AccountId,否则 SyncService 门禁拒绝(账户错配)。
        var lease = session.BeginAccountSession("u1", "me@example.com", new ServerOrigin(Server));
        session.SetUnlockedKey(userKey);
        var decryptor = new VaultDecryptor(crypto);
        var store = new FakeCacheStore();
        var cache = new VaultCacheService(store, decryptor, session);
        var sync = new SyncService(FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api), decryptor, session, cache);

        await sync.SyncAsync(lease, TestContext.Current.CancellationToken);

        Assert.False(session.DataStale);
        Assert.True(store.TryLoadVaultCache(out _));
        Assert.Equal(1, store.SaveCount);
    }

    private static string SyncJson() =>
        """{"object":"sync","profile":{"id":"u1","email":"me@example.com","name":"Me"},"folders":[],"ciphers":[]}""";
}
