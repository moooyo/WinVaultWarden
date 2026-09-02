using Core.Enums;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class MultiAccountStoreTests
{
    [Fact]
    public void PersistedSessionCollection_MigratesDeduplicatesAndCapsMostRecentAccounts()
    {
        var first = Session("account-0", "https://vault-0.example");
        var sessions = new[] { first, first with { RefreshToken = "duplicate" } }
            .Concat(Enumerable.Range(1, 6)
                .Select(index => Session($"account-{index}", $"https://vault-{index}.example")))
            .ToArray();

        var collection = PersistedSessionCollection.FromMostRecent(sessions);

        Assert.Equal(PersistedSessionCollection.MaximumSessions, collection.Sessions.Count);
        Assert.Equal("account-0", collection.Sessions[0].AccountId);
        Assert.Equal("account-4", collection.Sessions[^1].AccountId);
        Assert.Single(collection.Sessions, session => session.AccountId == "account-0");
        Assert.NotEqual("duplicate", collection.Sessions[0].RefreshToken);
    }

    [Fact]
    public void MemoryTokenStore_SavesActivatesAndRemovesIndependentAccounts()
    {
        var store = new MemoryTokenStore();
        var first = Session("account-a", "https://a.example");
        var second = Session("account-b", "https://b.example");

        store.Save(first);
        store.Save(second);

        Assert.True(store.TryLoad(out var active));
        Assert.Equal(second.AccountId, active.AccountId);
        Assert.Equal([second.AccountId, first.AccountId],
            store.GetSavedAccounts().Select(account => account.AccountId));

        Assert.True(store.TryActivate(first.AccountId, first.Endpoints!, out active));
        Assert.Equal(first.AccountId, active.AccountId);
        Assert.Equal([first.AccountId, second.AccountId],
            store.GetSavedAccounts().Select(account => account.AccountId));

        store.Save(first with { RefreshToken = "rotated" });
        Assert.Equal(2, store.GetSavedAccounts().Count);
        Assert.True(store.TryLoad(out active));
        Assert.Equal("rotated", active.RefreshToken);

        Assert.True(store.Remove(first.AccountId, first.Endpoints!));
        Assert.True(store.TryLoad(out active));
        Assert.Equal(second.AccountId, active.AccountId);
    }

    [Fact]
    public void MemoryTokenStore_TreatsSameAccountOnDifferentServersAsDistinct()
    {
        var store = new MemoryTokenStore();
        var first = Session("same-id", "https://a.example");
        var second = Session("same-id", "https://b.example");

        store.Save(first);
        store.Save(second);

        Assert.Equal(2, store.GetSavedAccounts().Count);
        Assert.True(store.TryActivate("same-id", first.Endpoints!, out var active));
        Assert.Equal("https://a.example", active.ServerUrl);
    }

    [Fact]
    public void VaultCacheCollection_MigratesAndDeduplicatesEntries()
    {
        var first = Cache("account-a", "https://a.example", "first");
        var duplicate = first with { SyncResponseJson = "duplicate" };
        var second = Cache("account-b", "https://b.example", "second");

        var collection = VaultCacheCollection.FromMostRecent([first, duplicate, second]);

        Assert.Equal(2, collection.Entries.Count);
        Assert.Equal("first", collection.Entries[0].SyncResponseJson);
        Assert.Equal("account-b", collection.Entries[1].AccountId);
    }

    private static PersistedSession Session(string accountId, string serverUrl) => new(
        PersistedSession.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        $"device-{accountId}",
        $"refresh-{accountId}",
        $"key-{accountId}",
        KdfType.Pbkdf2,
        600_000,
        null,
        null);

    private static VaultCacheEntry Cache(
        string accountId,
        string serverUrl,
        string payload) => new(
        VaultCacheEntry.CurrentSchemaVersion,
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        7,
        $"{accountId}@example.com",
        DateTimeOffset.UnixEpoch,
        payload);
}
