using System.Net;
using Api;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SyncServiceTests
{
    private const string Server = "https://vault.example";

    [Fact]
    public async Task SyncCoreAsync_OnSuccess_WritesCacheAndMarksSnapshotFresh()
    {
        var crypto = new CryptoService();
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => FakeHttpMessageHandler.Json(HttpStatusCode.OK, SyncJson()));
        var api = new ApiClient(new HttpClient(handler));
        api.SetBaseAddress(Server);
        var session = new VaultSession();
        session.SetUnlockedKey(userKey);
        var decryptor = new VaultDecryptor(crypto);
        var store = new FakeCacheStore();
        var cache = new VaultCacheService(store, decryptor, session);
        var sync = new SyncService(api, decryptor, session, cache);

        await sync.SyncCoreAsync(Server, TestContext.Current.CancellationToken);

        Assert.False(session.DataStale);
        Assert.True(store.TryLoadVaultCache(out _));
        Assert.Equal(1, store.SaveCount);
    }

    private static string SyncJson() =>
        """{"object":"sync","profile":{"id":"u1","email":"me@example.com","name":"Me"},"folders":[],"ciphers":[]}""";
}
