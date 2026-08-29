using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// Task 10: VaultWriteService.SaveCipherAsync(cipher, lease, ct) 必须以调用方传入的原始 lease 门禁。
// 当该 lease 在写操作跨 await 期间因 Lock/切换账户失效时,不得用新账户的 lease 同步、不得提交,
// 且最终抛 OperationCanceledException。防止陈旧 passkey 写请求把凭据落到已切换的账户上(A 不能写 B)。
public class VaultWriteServiceLeaseTests
{
    private static SymmetricCryptoKey NewKey() =>
        new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    private static Cipher NewLogin(string id = "") => new()
    {
        Id = id,
        Type = CipherType.Login,
        Name = "Item",
        Login = new CipherLogin("user", "pw", null, Array.Empty<CipherLoginUri>()),
    };

    // 在 create/update 上阻塞,直到手动放行;放行后忽略取消令牌照常"成功"完成写入。
    private sealed class BlockingWriteApi : IVaultWriteApiClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = new();

        public void Release() => _release.TrySetResult();

        private async Task BlockAsync(string call)
        {
            Calls.Add(call);
            Entered.TrySetResult();
            // 故意忽略 ct:模拟已发出的网络写请求不响应取消,只能等服务端回包。
            await _release.Task;
        }

        public Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default) => BlockAsync("create");
        public Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default) => BlockAsync("update");

        public Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default) => BlockAsync("create-folder");
        public Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // 记录 sync 是否被调用,以及每次调用携带的 lease(用于断言"绝不用新账户的 lease 同步")。
    private sealed class RecordingSync : ISyncService
    {
        public List<VaultSessionLease?> Leases { get; } = new();
        public int Calls => Leases.Count;
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        {
            Leases.Add(null);
            return Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
        }

        public Task<IReadOnlyList<Cipher>> SyncAsync(VaultSessionLease lease, CancellationToken ct = default)
        {
            Leases.Add(lease);
            if (Failure is not null)
                throw Failure;
            return Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
        }
    }

    private sealed class SwitchingProvider(
        VaultSession session,
        IVaultWriteApiClient api) : IVaultApiClientProvider
    {
        public TClient GetAnonymous<TClient>(ServerOrigin origin)
            where TClient : class, IAnonymousApiRole =>
            throw new NotSupportedException();

        public TClient GetAuthenticated<TClient>(VaultSessionLease lease)
            where TClient : class, IAuthenticatedApiRole
        {
            var next = session.BeginAccountSession(
                "account-b",
                "b@example.com",
                new ServerOrigin("https://b.example"));
            session.TryCommit(next, () => session.SetUnlockedKey(NewKey()));
            return (TClient)(object)api;
        }
    }

    private static IVaultWriteService NewService(VaultSession session, BlockingWriteApi api, RecordingSync sync) =>
        new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(api),
            new CipherEncryptor(new CryptoService()),
            sync,
            session);

    [Fact]
    public async Task SaveCipher_WithLease_StaleBeforeCall_A_CannotWriteB()
    {
        // 捕获账户 A 的 lease,随后切换到账户 B(使 A 失效),再用 A 的 lease 发起保存。
        var session = new VaultSession();
        var leaseA = session.BeginAccountSession("account-a", "a@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(NewKey());

        // 切换到 B。
        session.BeginAccountSession("account-b", "b@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(NewKey());

        var api = new BlockingWriteApi();
        var sync = new RecordingSync();
        var service = NewService(session, api, sync);

        // 提前放行:正确实现应在触达写 API 之前就以 A 的失效 lease 抛出取消;
        // 放行仅为防止(错误实现下)阻塞挂起,便于观察失败断言。
        api.Release();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.SaveCipherAsync(NewLogin(), leaseA, CancellationToken.None));

        // 用 A 的陈旧 lease 保存:绝不能触达写 API,更不能用 B 的 lease 同步。
        Assert.Empty(api.Calls);
        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_WithLease_LockDuringWrite_DoesNotSync_ThrowsCancelled()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(NewKey());

        var api = new BlockingWriteApi();
        var sync = new RecordingSync();
        var service = NewService(session, api, sync);

        var running = service.SaveCipherAsync(NewLogin(), lease, CancellationToken.None);
        await api.Entered.Task;
        session.Lock();
        api.Release();

        await Assert.ThrowsAsync<OperationCanceledException>(() => running);

        // 写请求已发出(create 记录),但锁定后不得再同步。
        Assert.Equal("create", Assert.Single(api.Calls));
        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_WithLease_SwitchAccountDuringWrite_DoesNotSyncUnderNewLease()
    {
        var session = new VaultSession();
        var leaseA = session.BeginAccountSession("account-a", "a@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(NewKey());

        var api = new BlockingWriteApi();
        var sync = new RecordingSync();
        var service = NewService(session, api, sync);

        var running = service.SaveCipherAsync(NewLogin(), leaseA, CancellationToken.None);
        await api.Entered.Task;

        // 阻塞期间切换到账户 B。
        session.BeginAccountSession("account-b", "b@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(NewKey());
        api.Release();

        await Assert.ThrowsAsync<OperationCanceledException>(() => running);

        Assert.Equal(0, sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_WithLease_HappyPath_WritesThenSyncsWithSameLease()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(NewKey());

        var api = new BlockingWriteApi();
        var sync = new RecordingSync();
        var service = NewService(session, api, sync);

        var running = service.SaveCipherAsync(NewLogin(), lease, CancellationToken.None);
        await api.Entered.Task;
        api.Release();
        await running;

        Assert.Equal("create", Assert.Single(api.Calls));
        // 同步走 lease 重载,且携带的正是请求的原始 lease。
        Assert.Same(lease, Assert.Single(sync.Leases));
    }

    [Fact]
    public async Task SaveCipher_ResyncOutOfMemory_IsNotWrappedAsCommittedWarning()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@test.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(NewKey());

        var api = new BlockingWriteApi();
        api.Release();
        var sync = new RecordingSync { Failure = new OutOfMemoryException() };
        var service = NewService(session, api, sync);

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            service.SaveCipherAsync(NewLogin(), lease, TestContext.Current.CancellationToken));

        Assert.Equal("create", Assert.Single(api.Calls));
        Assert.Same(lease, Assert.Single(sync.Leases));
    }

    [Fact]
    public async Task SaveFolder_AccountSwitchAfterEncryption_DoesNotWriteOrResync()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        session.TryCommit(lease, () => session.SetUnlockedKey(NewKey()));
        var api = new BlockingWriteApi();
        api.Release();
        var sync = new RecordingSync();
        var service = new VaultWriteService(
            new SwitchingProvider(session, api),
            new CipherEncryptor(new CryptoService()),
            sync,
            session);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.SaveFolderAsync(
                null,
                "Folder A",
                TestContext.Current.CancellationToken));

        Assert.Empty(api.Calls);
        Assert.Equal(0, sync.Calls);
    }
}
