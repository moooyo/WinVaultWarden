using Api;
using Api.Dtos;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class SyncCommitGateTests
{
    [Fact]
    public async Task SyncAsync_LockWhileHttpAwaiting_DropsSnapshotPrivateKeyAndCache()
    {
        var pending = new TaskCompletionSource<SyncResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ControlledReadonlyApiClient(async _ =>
        {
            entered.TrySetResult();
            return await pending.Task;
        });
        var session = NewUnlockedSession(out var lease);
        var store = new FakeCacheStore();
        var clients = FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api);
        var decryptor = new VaultDecryptor(new CryptoService());
        var cache = new VaultCacheService(store, decryptor, session);
        var service = new SyncService(clients, decryptor, session, cache);

        var running = service.SyncAsync(lease, CancellationToken.None);
        await entered.Task;
        session.Lock();
        pending.SetResult(new SyncResponse(
            "sync",
            new ProfileDto("account-a", "a@example.com", "A", null, "2.private"),
            [],
            []));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(session.Ciphers);
        Assert.Null(session.EncryptedPrivateKey);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task SyncAsync_ProfileAccountMismatch_RejectsEverything()
    {
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            new SyncResponse(
                "sync",
                new ProfileDto("account-b", "b@example.com", "B", null, "2.private"),
                [],
                [])));
        var session = NewUnlockedSession(out var lease);
        var store = new FakeCacheStore();
        var decryptor = new VaultDecryptor(new CryptoService());
        var service = new SyncService(
            FakeVaultApiClientProvider.ForAuthenticated<IReadonlyApiClient>(api),
            decryptor,
            session,
            new VaultCacheService(store, decryptor, session));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SyncAsync(lease, CancellationToken.None));

        Assert.Equal(0, store.SaveCount);
        Assert.Null(session.EncryptedPrivateKey);
    }

    [Fact]
    public void CacheLoad_WithStaleLease_DoesNotPopulatePlaintext()
    {
        var session = NewUnlockedSession(out var lease);
        Assert.True(session.TryCopyUserKey(lease, out var copiedKey));
        using var key = copiedKey;
        var store = new FakeCacheStore();
        var decryptor = new VaultDecryptor(new CryptoService());
        var cache = new VaultCacheService(store, decryptor, session);
        cache.Save(lease, new SyncResponse(
            "sync",
            new ProfileDto("account-a", "a@example.com", "A", null, null),
            [],
            []), DateTimeOffset.UnixEpoch);
        session.Lock();

        Assert.False(cache.TryLoadInto(lease, key!));
        Assert.Empty(session.Ciphers);
    }

    [Fact]
    public async Task BootstrapAsync_LockWhileDevicesAwaiting_DoesNotUnlock()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var devices = new TaskCompletionSource<ListResponse<DeviceDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ControlledReadonlyApiClient(_ => Task.FromResult(
            new SyncResponse(
                "sync",
                new ProfileDto("account-a", "a@example.com", "A", null, null),
                [],
                [])))
        {
            GetDevices = async _ =>
            {
                entered.TrySetResult();
                return await devices.Task;
            },
        };
        var clients = FakeVaultApiClientProvider
            .ForAuthenticated<IReadonlyApiClient>(api);
        var session = NewUnlockedSession(out var lease);
        var decryptor = new VaultDecryptor(new CryptoService());
        var cache = new VaultCacheService(new FakeCacheStore(), decryptor, session);
        var sync = new SyncService(clients, decryptor, session, cache);
        var bootstrap = new VaultBootstrapper(sync, clients, session);

        var running = bootstrap.BootstrapAsync(lease, CancellationToken.None);
        await entered.Task;
        session.Lock();
        devices.SetResult(new ListResponse<DeviceDto>(
            [],
            null,
            "list"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(session.Devices);
        Assert.Equal(VaultState.Locked, session.State);
    }

    private static VaultSession NewUnlockedSession(out VaultSessionLease lease)
    {
        var session = new VaultSession();
        lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        var key = new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        Assert.True(session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(key);
            session.SetTokens("access-a", "refresh-a");
        }));
        return session;
    }
}
