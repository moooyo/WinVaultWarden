using System.Text;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

/// <summary>
/// NotificationDispatcher 路由分发测试。
/// 使用真实 CryptoService + VaultDecryptor + VaultSession，
/// 配合 FakeAttachmentApiClient / FakeNotificationReadonlyApiClient / FakeNotificationSyncService。
/// 遵循 TDD：先写测试（RED），再写实现（GREEN）。
///
/// Task 9：DispatchAsync 接受启动时捕获的显式 lease；补丁与事件只在同一 TryCommit 内、
/// lease 仍 current 时提交。Lock/Logout/账户切换在 await 期间发生时，旧响应绝不补丁/触发事件。
/// </summary>
public class NotificationDispatcherTests
{
    // ─────────────────────────── 测试基础设施 ───────────────────────────

    private readonly CryptoService _crypto = new();
    private readonly SymmetricCryptoKey _userKey =
        new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    private string Enc(string plaintext, SymmetricCryptoKey key)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), key).ToString();

    /// <summary>构建一个已加密的 CipherDto，Name/Notes 用 userKey 加密。</summary>
    private CipherDto MakeEncryptedCipherDto(string id, string name) => new(
        Id: id,
        Type: (int)CipherType.Login,
        Name: Enc(name, _userKey),
        Notes: Enc("note", _userKey),
        Key: null,
        OrganizationId: null,
        FolderId: null,
        Favorite: false,
        Reprompt: 0,
        Login: new LoginDto(Enc("user", _userKey), Enc("pass", _userKey), null, null),
        Card: null,
        Identity: null,
        SecureNote: null,
        SshKey: null,
        Fields: null,
        CreationDate: DateTimeOffset.UtcNow,
        RevisionDate: DateTimeOffset.UtcNow,
        DeletedDate: null);

    /// <summary>构建一个已加密的 FolderDto，Name 用 userKey 加密。</summary>
    private FolderDto MakeEncryptedFolderDto(string id, string name) => new(
        Id: id,
        Name: Enc(name, _userKey),
        RevisionDate: DateTimeOffset.UtcNow);

    /// <summary>创建已解锁（UserKey 非空）的 VaultSession，并返回其启动租约。</summary>
    private VaultSession NewUnlockedSession(out VaultSessionLease lease)
    {
        var session = new VaultSession();
        lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        var sessionKey = new SymmetricCryptoKey(_userKey.FullKey.ToArray());
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(sessionKey);
            session.SetTokens("access-a", "refresh-a");
        });
        return session;
    }

    /// <summary>
    /// 创建已锁定（UserKey 为 null）但仍有活跃账户的 VaultSession，返回锁定后的当前租约。
    /// 锁定会轮换 generation，故返回的是锁定后仍 current 的新租约。
    /// </summary>
    private VaultSession NewLockedSession(out VaultSessionLease lease)
    {
        var session = new VaultSession();
        var initial = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        var sessionKey = new SymmetricCryptoKey(_userKey.FullKey.ToArray());
        session.TryCommit(initial, () =>
        {
            session.SetUnlockedKey(sessionKey);
            session.SetTokens("access-a", "refresh-a");
        });
        lease = session.Lock()!;
        return session;
    }

    private static FakeVaultApiClientProvider ClientsWith(
        IAttachmentApiClient? cipherApi = null,
        IReadonlyApiClient? readApi = null) =>
        new FakeVaultApiClientProvider()
            .AddAuthenticated<IAttachmentApiClient>(cipherApi ?? new FakeAttachmentApiClient())
            .AddAuthenticated<IReadonlyApiClient>(readApi ?? new FakeNotificationReadonlyApiClient());

    private NotificationDispatcher NewDispatcher(
        VaultSession session,
        IVaultApiClientProvider? clients = null,
        ISyncService? sync = null,
        IDeviceIdentifierProvider? deviceIdentifierProvider = null,
        ISessionTerminationSink? termination = null,
        ITokenStore? tokenStore = null,
        VaultCacheService? cache = null)
    {
        clients ??= ClientsWith();
        return new NotificationDispatcher(
            clients,
            new VaultDecryptor(_crypto),
            session,
            sync ?? new FakeNotificationSyncService(),
            termination: termination,
            deviceIdentifierProvider: deviceIdentifierProvider,
            tokenStore: tokenStore,
            cache: cache);
    }

    // ─────────────────────────── 测试场景 ───────────────────────────

    /// <summary>
    /// SyncCipherCreate 推送：
    /// 期望 dispatcher 调用 GetCipherAsync → 解密 → UpsertCipher，
    /// session.Ciphers 中出现该条目，且 VaultChanged 事件被触发。
    /// </summary>
    [Fact]
    public async Task CipherCreate_Push_UpsertsCipherAndRaisesVaultChanged()
    {
        var dto = MakeEncryptedCipherDto("c-1", "GitHub");
        var cipherApi = new FakeAttachmentApiClient { GetCipherResult = dto };
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, ClientsWith(cipherApi: cipherApi));

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncCipherCreate, "c-1"),
            CancellationToken.None);

        Assert.Equal("c-1", cipherApi.LastGetCipherId);
        var cipher = Assert.Single(session.Ciphers, c => c.Id == "c-1");
        Assert.Equal("GitHub", cipher.Name);
        Assert.Equal(1, vaultChangedCount);
    }

    [Fact]
    public async Task OrganizationCipherPush_UsesFullSyncInsteadOfUserKeyDecryption()
    {
        var dto = MakeEncryptedCipherDto("c-org", "Shared") with { OrganizationId = "org-1" };
        var cipherApi = new FakeAttachmentApiClient { GetCipherResult = dto };
        var session = NewUnlockedSession(out var lease);
        var sync = new FakeNotificationSyncService();
        var dispatcher = NewDispatcher(
            session,
            ClientsWith(cipherApi: cipherApi),
            sync);
        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncCipherCreate, "c-org"),
            CancellationToken.None);

        Assert.Equal(1, sync.SyncCalls);
        Assert.Same(lease, Assert.Single(sync.Leases));
        Assert.Empty(session.Ciphers);
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncCipherUpdate 推送：
    /// 与 Create 相同路径（GET → 解密 → Upsert）；覆盖已存在的条目，名称更新。
    /// </summary>
    [Fact]
    public async Task CipherUpdate_Push_ReplacesExistingCipherAndRaisesVaultChanged()
    {
        var session = NewUnlockedSession(out var lease);
        session.UpsertCipher(new Cipher { Id = "c-1", Type = CipherType.Login, Name = "Old Name" });

        var dto = MakeEncryptedCipherDto("c-1", "New Name");
        var cipherApi = new FakeAttachmentApiClient { GetCipherResult = dto };
        var dispatcher = NewDispatcher(session, ClientsWith(cipherApi: cipherApi));

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncCipherUpdate, "c-1"),
            CancellationToken.None);

        Assert.Equal(1, session.Ciphers.Count);
        var cipher = Assert.Single(session.Ciphers, c => c.Id == "c-1");
        Assert.Equal("New Name", cipher.Name);
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncLoginDelete 推送：
    /// 从 session.Ciphers 中移除指定 Id，VaultChanged 触发，无 GET 请求。
    /// </summary>
    [Fact]
    public async Task CipherDelete_Push_RemovesCipherAndRaisesVaultChanged()
    {
        var session = NewUnlockedSession(out var lease);
        session.UpsertCipher(new Cipher { Id = "c-del", Type = CipherType.Login, Name = "ToDelete" });

        var cipherApi = new FakeAttachmentApiClient();
        var dispatcher = NewDispatcher(session, ClientsWith(cipherApi: cipherApi));

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncLoginDelete, "c-del"),
            CancellationToken.None);

        Assert.Empty(cipherApi.Calls);
        Assert.Empty(session.Ciphers);
        Assert.Equal(1, vaultChangedCount);
    }

    [Fact]
    public async Task ModernCipherDelete_Push_RemovesCipherAndRaisesVaultChanged()
    {
        var session = NewUnlockedSession(out var lease);
        session.UpsertCipher(new Cipher { Id = "c-modern", Type = CipherType.Login, Name = "Delete" });
        var dispatcher = NewDispatcher(session);
        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncCipherDelete, "c-modern"),
            CancellationToken.None);

        Assert.Empty(session.Ciphers);
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncFolderCreate 推送：
    /// 调用 GetFolderAsync → 解密 → UpsertFolder，VaultChanged 触发。
    /// </summary>
    [Fact]
    public async Task FolderCreate_Push_UpsertsFolderAndRaisesVaultChanged()
    {
        var dto = MakeEncryptedFolderDto("f-1", "Work");
        var readApi = new FakeNotificationReadonlyApiClient { GetFolderResult = dto };
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, ClientsWith(readApi: readApi));

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncFolderCreate, "f-1"),
            CancellationToken.None);

        Assert.Equal("f-1", readApi.LastGetFolderId);
        var folder = Assert.Single(session.Folders, f => f.Id == "f-1");
        Assert.Equal("Work", folder.Name);
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncFolderUpdate 推送：替换已存在文件夹的名称，VaultChanged 触发。
    /// </summary>
    [Fact]
    public async Task FolderUpdate_Push_ReplacesExistingFolderAndRaisesVaultChanged()
    {
        var session = NewUnlockedSession(out var lease);
        session.UpsertFolder(new Folder { Id = "f-1", Name = "Old Folder" });

        var dto = MakeEncryptedFolderDto("f-1", "Renamed Folder");
        var readApi = new FakeNotificationReadonlyApiClient { GetFolderResult = dto };
        var dispatcher = NewDispatcher(session, ClientsWith(readApi: readApi));

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncFolderUpdate, "f-1"),
            CancellationToken.None);

        Assert.Equal(1, session.Folders.Count);
        var folder = Assert.Single(session.Folders, f => f.Id == "f-1");
        Assert.Equal("Renamed Folder", folder.Name);
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncFolderDelete 推送：
    /// 从 session.Folders 中移除指定 Id，VaultChanged 触发，无 GET 请求。
    /// </summary>
    [Fact]
    public async Task FolderDelete_Push_RemovesFolderAndRaisesVaultChanged()
    {
        var session = NewUnlockedSession(out var lease);
        session.UpsertFolder(new Folder { Id = "f-del", Name = "Bye" });
        session.UpsertCipher(new Cipher
        {
            Id = "c-in-folder",
            Type = CipherType.Login,
            Name = "X",
            FolderId = "f-del",
        });

        var readApi = new FakeNotificationReadonlyApiClient();
        var dispatcher = NewDispatcher(session, ClientsWith(readApi: readApi));

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncFolderDelete, "f-del"),
            CancellationToken.None);

        Assert.Empty(readApi.Calls);
        Assert.Empty(session.Folders);
        var cipher = Assert.Single(session.Ciphers);
        Assert.Null(cipher.FolderId);
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncSendCreate/Update/Delete 推送：
    /// 仅触发 SendsChanged，不触发 VaultChanged，不调用任何 GET API。
    /// </summary>
    [Theory]
    [InlineData((int)UpdateType.SyncSendCreate)]
    [InlineData((int)UpdateType.SyncSendUpdate)]
    [InlineData((int)UpdateType.SyncSendDelete)]
    public async Task SendPush_RaisesSendsChangedOnly_NoGetCall(int type)
    {
        var cipherApi = new FakeAttachmentApiClient();
        var readApi = new FakeNotificationReadonlyApiClient();
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, ClientsWith(cipherApi: cipherApi, readApi: readApi));

        var sendsChangedCount = 0;
        var vaultChangedCount = 0;
        dispatcher.SendsChanged += () => sendsChangedCount++;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage(type, "send-id"),
            CancellationToken.None);

        Assert.Equal(1, sendsChangedCount);
        Assert.Equal(0, vaultChangedCount);
        Assert.Empty(cipherApi.Calls);
        Assert.Empty(readApi.Calls);
    }

    /// <summary>
    /// SyncCiphers/SyncVault/SyncOrgKeys/SyncSettings 推送：
    /// 调用 SyncService.SyncAsync，然后触发 VaultChanged。
    /// </summary>
    [Theory]
    [InlineData((int)UpdateType.SyncCiphers)]
    [InlineData((int)UpdateType.SyncVault)]
    [InlineData((int)UpdateType.SyncOrgKeys)]
    [InlineData((int)UpdateType.SyncSettings)]
    [InlineData((int)UpdateType.SyncOrganizations)]
    [InlineData((int)UpdateType.SyncOrganizationStatusChanged)]
    [InlineData((int)UpdateType.SyncOrganizationCollectionSettingChanged)]
    [InlineData((int)UpdateType.SyncPolicy)]
    public async Task BulkSyncPush_CallsSyncAsyncAndRaisesVaultChanged(int type)
    {
        var sync = new FakeNotificationSyncService();
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, sync: sync);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage(type, null),
            CancellationToken.None);

        Assert.Equal(1, sync.SyncCalls);
        Assert.Equal(1, vaultChangedCount);
    }

    [Fact]
    public async Task SyncOrgKeys_AfterSuccessfulSyncRequestsConnectionRebuild()
    {
        var sync = new FakeNotificationSyncService();
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, sync: sync);
        var rebuildCount = 0;
        dispatcher.ConnectionRebuildRequested += () => rebuildCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncOrgKeys, null),
            CancellationToken.None);

        Assert.Equal(1, sync.SyncCalls);
        Assert.Equal(1, rebuildCount);
    }

    [Fact]
    public async Task SyncOrgKeys_WhenSyncFailsDoesNotRequestConnectionRebuild()
    {
        var sync = new FakeNotificationSyncService
        {
            SyncException = new HttpRequestException("sync failed"),
        };
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, sync: sync);
        var rebuildCount = 0;
        dispatcher.ConnectionRebuildRequested += () => rebuildCount++;

        await Assert.ThrowsAsync<HttpRequestException>(() => dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncOrgKeys, null),
            CancellationToken.None));

        Assert.Equal(0, rebuildCount);
    }

    [Fact]
    public async Task CurrentContextNotification_IsIgnored()
    {
        const string deviceId = "1a3eb29f-fdc6-4074-9d74-1b439aa3ac38";
        var sync = new FakeNotificationSyncService();
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(
            session,
            sync: sync,
            deviceIdentifierProvider: new FixedDeviceIdentifierProvider(deviceId));

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage(
                (int)UpdateType.SyncVault,
                null,
                ContextId: deviceId.ToUpperInvariant()),
            CancellationToken.None);

        Assert.Equal(0, sync.SyncCalls);
    }

    [Fact]
    public async Task NotificationForDifferentAccount_IsIgnored()
    {
        var sync = new FakeNotificationSyncService();
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, sync: sync);

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage(
                (int)UpdateType.SyncVault,
                null,
                UserId: "account-b"),
            CancellationToken.None);

        Assert.Equal(0, sync.SyncCalls);
    }

    /// <summary>
    /// AuthRequest 推送：仅触发 AuthRequestsChanged，不触发其他事件。
    /// </summary>
    [Fact]
    public async Task AuthRequest_Push_RaisesAuthRequestsChangedOnly()
    {
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session);

        var authChangedCount = 0;
        var vaultChangedCount = 0;
        dispatcher.AuthRequestsChanged += () => authChangedCount++;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.AuthRequest, null),
            CancellationToken.None);

        Assert.Equal(1, authChangedCount);
        Assert.Equal(0, vaultChangedCount);
    }

    /// <summary>
    /// LogOut 推送（已解锁状态）：触发 LoggedOut 事件，不触发其他事件。
    /// </summary>
    [Fact]
    public async Task LogOut_Push_WhenUnlocked_RaisesLoggedOut()
    {
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session);

        var loggedOutCount = 0;
        var vaultChangedCount = 0;
        dispatcher.LoggedOut += () => loggedOutCount++;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.LogOut, null),
            CancellationToken.None);

        Assert.Equal(1, loggedOutCount);
        Assert.Equal(0, vaultChangedCount);
    }

    /// <summary>
    /// LogOut 推送（已锁定状态，UserKey 为 null）：
    /// 即使 session 已锁定，只要租约仍 current，仍必须触发 LoggedOut 事件（例外规则）。
    /// </summary>
    [Fact]
    public async Task LogOut_Push_WhenLocked_StillRaisesLoggedOut()
    {
        var session = NewLockedSession(out var lease);
        var dispatcher = NewDispatcher(session);

        var loggedOutCount = 0;
        dispatcher.LoggedOut += () => loggedOutCount++;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.LogOut, null),
            CancellationToken.None);

        Assert.Equal(1, loggedOutCount);
    }

    [Fact]
    public async Task LogOut_PurgesExactBindingBeforePublishingTermination()
    {
        var session = NewUnlockedSession(out var lease);
        var tokenStore = new RecordingRemovalTokenStore();
        var cacheStore = new FakeCacheStore();
        var cache = new VaultCacheService(
            cacheStore,
            new VaultDecryptor(_crypto),
            session);
        var termination = new SessionTerminationSignal();
        SessionTerminationNotice? notice = null;
        var purgeObservedBeforeTermination = false;
        termination.TerminationRequested += value =>
        {
            notice = value;
            purgeObservedBeforeTermination = tokenStore.RemovedBindings.Count == 1
                && cacheStore.ClearCount == 1;
        };
        var dispatcher = NewDispatcher(
            session,
            termination: termination,
            tokenStore: tokenStore,
            cache: cache);

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.LogOut, null),
            CancellationToken.None);

        var removed = Assert.Single(tokenStore.RemovedBindings);
        Assert.Equal(lease.AccountId, removed.AccountId);
        Assert.Equal(lease.Endpoints, removed.Endpoints);
        var cleared = Assert.Single(cacheStore.ClearedBindings);
        Assert.Equal(lease.AccountId, cleared.AccountId);
        Assert.Equal(lease.Endpoints, cleared.Endpoints);
        Assert.True(purgeObservedBeforeTermination);
        Assert.Equal(SessionTerminationReason.RemoteLogout, notice?.Reason);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task LogOut_PersistenceFailuresStillLockAndPublishTermination()
    {
        var session = NewUnlockedSession(out var lease);
        var tokenStore = new RecordingRemovalTokenStore
        {
            RemoveResult = false,
            RemoveException = new IOException("token removal failed"),
        };
        var cacheStore = new FakeCacheStore
        {
            BeforeClear = () => throw new UnauthorizedAccessException("cache removal failed"),
        };
        var cache = new VaultCacheService(
            cacheStore,
            new VaultDecryptor(_crypto),
            session);
        var termination = new SessionTerminationSignal();
        var notices = 0;
        termination.TerminationRequested += _ => notices++;
        var loggedOut = 0;
        var dispatcher = NewDispatcher(
            session,
            termination: termination,
            tokenStore: tokenStore,
            cache: cache);
        dispatcher.LoggedOut += () => loggedOut++;

        var exception = await Assert.ThrowsAsync<AuthSessionPersistenceException>(() =>
            dispatcher.DispatchAsync(
                lease,
                new NotificationMessage((int)UpdateType.LogOut, null),
                CancellationToken.None));

        Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Single(tokenStore.RemovedBindings);
        Assert.Equal(1, cacheStore.ClearAttemptCount);
        Assert.Equal(1, notices);
        Assert.Equal(1, loggedOut);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task LogOut_RemoveReturningFalseStillLocksSignalsAndReportsFailure()
    {
        var session = NewUnlockedSession(out var lease);
        var tokenStore = new RecordingRemovalTokenStore { RemoveResult = false };
        var termination = new SessionTerminationSignal();
        var notices = 0;
        termination.TerminationRequested += _ => notices++;
        var dispatcher = NewDispatcher(
            session,
            termination: termination,
            tokenStore: tokenStore);

        var exception = await Assert.ThrowsAsync<AuthSessionPersistenceException>(() =>
            dispatcher.DispatchAsync(
                lease,
                new NotificationMessage((int)UpdateType.LogOut, null),
                CancellationToken.None));

        Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(1, notices);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.False(session.IsCurrent(lease));
    }

    [Fact]
    public async Task LogOut_InvalidatesLeaseBeforeAccountScopedPurge()
    {
        var session = NewUnlockedSession(out var lease);
        var observedOldLeaseCurrent = true;
        var observedState = VaultState.Unlocked;
        var tokenStore = new RecordingRemovalTokenStore
        {
            BeforeRemove = () =>
            {
                observedOldLeaseCurrent = session.IsCurrent(lease);
                observedState = session.State;
            },
        };
        var dispatcher = NewDispatcher(session, tokenStore: tokenStore);

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.LogOut, null),
            CancellationToken.None);

        Assert.False(observedOldLeaseCurrent);
        Assert.Equal(VaultState.Locked, observedState);
        Assert.Single(tokenStore.RemovedBindings);
    }

    /// <summary>
    /// 锁定状态（UserKey = null）下的 SyncCipherCreate 推送：
    /// 期望完全忽略，不调用 API，不触发任何事件。
    /// </summary>
    [Fact]
    public async Task CipherCreate_WhenLocked_IsNoOpNoEvent()
    {
        var cipherApi = new FakeAttachmentApiClient();
        var session = NewLockedSession(out var lease);
        var dispatcher = NewDispatcher(session, ClientsWith(cipherApi: cipherApi));

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;
        dispatcher.SendsChanged += () => eventFired = true;
        dispatcher.AuthRequestsChanged += () => eventFired = true;
        dispatcher.LoggedOut += () => eventFired = true;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncCipherCreate, "c-locked"),
            CancellationToken.None);

        Assert.Empty(cipherApi.Calls);
        Assert.False(eventFired);
    }

    /// <summary>
    /// SyncCipherCreate 推送但 EntityId 为 null：应忽略，不调用 API，不触发事件。
    /// </summary>
    [Fact]
    public async Task CipherCreate_NullEntityId_IsNoOp()
    {
        var cipherApi = new FakeAttachmentApiClient();
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, ClientsWith(cipherApi: cipherApi));

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncCipherCreate, null),
            CancellationToken.None);

        Assert.Empty(cipherApi.Calls);
        Assert.False(eventFired);
    }

    /// <summary>
    /// None / AuthRequestResponse 及未知类型推送：完全忽略，无事件。
    /// </summary>
    [Theory]
    [InlineData((int)UpdateType.None)]
    [InlineData((int)UpdateType.AuthRequestResponse)]
    [InlineData(999)] // 未知类型
    public async Task UnknownOrIgnoredType_IsNoOp(int type)
    {
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session);

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;
        dispatcher.SendsChanged += () => eventFired = true;
        dispatcher.AuthRequestsChanged += () => eventFired = true;
        dispatcher.LoggedOut += () => eventFired = true;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage(type, "some-id"),
            CancellationToken.None);

        Assert.False(eventFired);
    }

    /// <summary>
    /// SyncFolderCreate/Update/Delete 推送但 EntityId 为 null：
    /// 应完全忽略——不调用 GetFolderAsync，不触发任何事件。
    /// </summary>
    [Theory]
    [InlineData((int)UpdateType.SyncFolderCreate)]
    [InlineData((int)UpdateType.SyncFolderUpdate)]
    [InlineData((int)UpdateType.SyncFolderDelete)]
    public async Task FolderPush_NullEntityId_IsNoOp(int type)
    {
        var readApi = new FakeNotificationReadonlyApiClient();
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session, ClientsWith(readApi: readApi));

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage(type, null),
            CancellationToken.None);

        Assert.Empty(readApi.Calls);
        Assert.False(eventFired);
    }

    /// <summary>
    /// 锁定状态（UserKey = null）下的 SyncFolderCreate、SyncSendCreate、SyncCiphers 推送：
    /// 期望完全忽略——不调用任何 API，不触发任何事件。
    /// </summary>
    [Theory]
    [InlineData((int)UpdateType.SyncFolderCreate)]
    [InlineData((int)UpdateType.SyncSendCreate)]
    [InlineData((int)UpdateType.SyncCiphers)]
    public async Task Push_WhenLocked_IsNoOpNoEvent(int type)
    {
        var cipherApi = new FakeAttachmentApiClient();
        var readApi = new FakeNotificationReadonlyApiClient();
        var sync = new FakeNotificationSyncService();
        var session = NewLockedSession(out var lease);
        var dispatcher = NewDispatcher(session, ClientsWith(cipherApi: cipherApi, readApi: readApi), sync);

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;
        dispatcher.SendsChanged += () => eventFired = true;
        dispatcher.AuthRequestsChanged += () => eventFired = true;
        dispatcher.LoggedOut += () => eventFired = true;

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage(type, "some-id"),
            CancellationToken.None);

        Assert.Empty(cipherApi.Calls);
        Assert.Empty(readApi.Calls);
        Assert.Equal(0, sync.SyncCalls);
        Assert.False(eventFired);
    }

    // ─────────────────────────── Task 9 竞态门禁 ───────────────────────────

    /// <summary>
    /// CipherUpdate 推送在 GET 期间 Lock：旧响应回来后既不补丁也不触发事件，且抛出取消异常。
    /// </summary>
    [Fact]
    public async Task CipherUpdate_LockDuringGet_DoesNotPatchOrRaise()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<CipherDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new BlockingAttachmentApiClient(async _ =>
        {
            entered.TrySetResult();
            return await pending.Task;
        });
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(
            session,
            FakeVaultApiClientProvider.ForAuthenticated<IAttachmentApiClient>(api));
        var raised = 0;
        dispatcher.VaultChanged += () => raised++;

        var running = dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncCipherUpdate, "c-1"),
            CancellationToken.None);
        await entered.Task;
        session.Lock();
        pending.SetResult(MakeEncryptedCipherDto("c-1", "Late secret"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(session.Ciphers);
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// 陈旧 lease（账户已切换到 B）上的 Send 推送：不触发 SendsChanged。
    /// </summary>
    [Fact]
    public async Task StaleSendPush_DoesNotRaiseSendsChanged()
    {
        var session = NewUnlockedSession(out var lease);
        var dispatcher = NewDispatcher(session);
        var raised = 0;
        dispatcher.SendsChanged += () => raised++;
        session.BeginAccountSession(
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"));

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncSendUpdate, "s-1"),
            CancellationToken.None);

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// FolderUpdate 推送在 GET 期间 Logout：旧响应回来后既不补丁也不触发事件，且抛出取消异常。
    /// </summary>
    [Fact]
    public async Task FolderUpdate_LogoutDuringGet_DoesNotPatchOrRaise()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<FolderDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new ControlledReadonlyApiClient(
            _ => throw new NotSupportedException())
        {
            GetFolder = async (_, _) =>
            {
                entered.TrySetResult();
                return await pending.Task;
            },
        };
        var session = NewUnlockedSession(out var lease);
        var clients = new FakeVaultApiClientProvider()
            .AddAuthenticated<IAttachmentApiClient>(
                new FakeAttachmentApiClient())
            .AddAuthenticated<IReadonlyApiClient>(read);
        var dispatcher = NewDispatcher(session, clients);
        var raised = 0;
        dispatcher.VaultChanged += () => raised++;

        var running = dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncFolderUpdate, "f-1"),
            CancellationToken.None);
        await entered.Task;
        session.Logout();
        pending.SetResult(MakeEncryptedFolderDto("f-1", "Late folder"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(session.Folders);
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// 全量同步推送：透传启动租约给 SyncService（不回退到无 lease 重载）。
    /// </summary>
    [Fact]
    public async Task BulkSyncPush_UsesNotificationLease()
    {
        var session = NewUnlockedSession(out var lease);
        var sync = new FakeNotificationSyncService();
        var dispatcher = NewDispatcher(session, sync: sync);

        await dispatcher.DispatchAsync(
            lease,
            new NotificationMessage((int)UpdateType.SyncVault, null),
            CancellationToken.None);

        Assert.Same(lease, Assert.Single(sync.Leases));
    }
}

// ─────────────────────────── 测试专用 Fake 类 ───────────────────────────

/// <summary>
/// IReadonlyApiClient 的最小化测试替身，只实现 GetFolderAsync。
/// 其余方法抛出 NotImplementedException（本测试中不会被调用）。
/// </summary>
public sealed class FakeNotificationReadonlyApiClient : IReadonlyApiClient
{
    public List<string> Calls { get; } = new();
    public string? LastGetFolderId { get; private set; }
    public FolderDto GetFolderResult { get; set; } = null!;

    public Task<FolderDto> GetFolderAsync(string folderId, CancellationToken ct = default)
    {
        Calls.Add("get-folder");
        LastGetFolderId = folderId;
        return Task.FromResult(GetFolderResult);
    }

    // 以下均为本测试不使用的接口方法
    public Task<Api.Dtos.ConfigResponse> GetConfigAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.PreloginResponse> PreloginAsync(string email, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.ConnectTokenResult> ConnectTokenAsync(Api.Dtos.ConnectTokenRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.SyncResponse> GetSyncAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.ListResponse<Api.Dtos.DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task SendTwoFactorEmailLoginAsync(Api.Dtos.SendEmailLoginRequest request, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>
/// ISyncService 的最小化测试替身，记录调用次数与显式租约。
/// 无 lease 重载被禁用——通知路径必须使用显式租约重载。
/// </summary>
public sealed class FakeNotificationSyncService : Core.Services.ISyncService
{
    public int SyncCalls { get; private set; }
    public List<VaultSessionLease> Leases { get; } = new();
    public Exception? SyncException { get; set; }
    public Exception? NextSyncException { get; set; }

    public Task<IReadOnlyList<Core.Models.Cipher>> SyncAsync(
        VaultSessionLease lease, CancellationToken ct = default)
    {
        SyncCalls++;
        Leases.Add(lease);
        if (NextSyncException is { } next)
        {
            NextSyncException = null;
            return Task.FromException<IReadOnlyList<Core.Models.Cipher>>(next);
        }
        return SyncException is null
            ? Task.FromResult<IReadOnlyList<Core.Models.Cipher>>(Array.Empty<Core.Models.Cipher>())
            : Task.FromException<IReadOnlyList<Core.Models.Cipher>>(SyncException);
    }

    public Task<IReadOnlyList<Core.Models.Cipher>> SyncAsync(CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "Notification tests must use the explicit lease overload.");

}

public sealed class FixedDeviceIdentifierProvider(string identifier) : IDeviceIdentifierProvider
{
    public string GetDeviceIdentifier() => identifier;
}

public sealed class RecordingRemovalTokenStore : ITokenStore
{
    public bool RemoveResult { get; init; } = true;
    public Exception? RemoveException { get; init; }
    public Action? BeforeRemove { get; init; }
    public List<(string AccountId, ServerEndpoints Endpoints)> RemovedBindings { get; } = [];

    public bool TryLoad(out PersistedSession session)
    {
        session = null!;
        return false;
    }

    public void Save(PersistedSession session)
    {
    }

    public void Clear()
    {
    }

    public bool Remove(string accountId, ServerEndpoints endpoints)
    {
        RemovedBindings.Add((accountId, endpoints));
        BeforeRemove?.Invoke();
        if (RemoveException is not null)
            throw RemoveException;
        return RemoveResult;
    }
}
