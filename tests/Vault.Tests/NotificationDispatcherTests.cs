using System.Text;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

/// <summary>
/// NotificationDispatcher 路由分发测试。
/// 使用真实 CryptoService + VaultDecryptor + VaultSession，
/// 配合 FakeAttachmentApiClient / FakeNotificationReadonlyApiClient / FakeNotificationSyncService。
/// 遵循 TDD：先写测试（RED），再写实现（GREEN）。
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

    /// <summary>创建已解锁（UserKey 非空）的 VaultSession。</summary>
    private VaultSession NewUnlockedSession()
    {
        var session = new VaultSession();
        session.SetUnlockedKey(_userKey);
        return session;
    }

    /// <summary>创建已锁定（UserKey 为 null）的 VaultSession。</summary>
    private static VaultSession NewLockedSession() => new();

    private NotificationDispatcher NewDispatcher(
        VaultSession session,
        FakeAttachmentApiClient? cipherApi = null,
        FakeNotificationReadonlyApiClient? readApi = null,
        FakeNotificationSyncService? sync = null)
    {
        var decryptor = new VaultDecryptor(_crypto);
        return new NotificationDispatcher(
            cipherApi ?? new FakeAttachmentApiClient(),
            readApi ?? new FakeNotificationReadonlyApiClient(),
            decryptor,
            session,
            sync ?? new FakeNotificationSyncService());
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
        // Arrange
        var dto = MakeEncryptedCipherDto("c-1", "GitHub");
        var cipherApi = new FakeAttachmentApiClient { GetCipherResult = dto };
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session, cipherApi: cipherApi);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncCipherCreate, "c-1"),
            CancellationToken.None);

        // Assert — API 被调用
        Assert.Equal("c-1", cipherApi.LastGetCipherId);

        // Assert — 条目被插入 session
        var cipher = Assert.Single(session.Ciphers, c => c.Id == "c-1");
        Assert.Equal("GitHub", cipher.Name);

        // Assert — VaultChanged 事件触发一次
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncCipherUpdate 推送：
    /// 与 Create 相同路径（GET → 解密 → Upsert）；覆盖已存在的条目，名称更新。
    /// </summary>
    [Fact]
    public async Task CipherUpdate_Push_ReplacesExistingCipherAndRaisesVaultChanged()
    {
        // Arrange — 预先植入旧版本条目
        var session = NewUnlockedSession();
        session.UpsertCipher(new Cipher { Id = "c-1", Type = CipherType.Login, Name = "Old Name" });

        var dto = MakeEncryptedCipherDto("c-1", "New Name");
        var cipherApi = new FakeAttachmentApiClient { GetCipherResult = dto };
        var dispatcher = NewDispatcher(session, cipherApi: cipherApi);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncCipherUpdate, "c-1"),
            CancellationToken.None);

        // Assert — 条目数量不变（替换而非追加）
        Assert.Equal(1, session.Ciphers.Count);

        // Assert — 名称已更新
        var cipher = Assert.Single(session.Ciphers, c => c.Id == "c-1");
        Assert.Equal("New Name", cipher.Name);

        // Assert — 事件触发一次
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncLoginDelete 推送：
    /// 从 session.Ciphers 中移除指定 Id，VaultChanged 触发，无 GET 请求。
    /// </summary>
    [Fact]
    public async Task CipherDelete_Push_RemovesCipherAndRaisesVaultChanged()
    {
        // Arrange
        var session = NewUnlockedSession();
        session.UpsertCipher(new Cipher { Id = "c-del", Type = CipherType.Login, Name = "ToDelete" });

        var cipherApi = new FakeAttachmentApiClient();
        var dispatcher = NewDispatcher(session, cipherApi: cipherApi);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncLoginDelete, "c-del"),
            CancellationToken.None);

        // Assert — 不应调用 GetCipherAsync
        Assert.Empty(cipherApi.Calls);

        // Assert — 条目已从 session 移除
        Assert.Empty(session.Ciphers);

        // Assert — VaultChanged 触发
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncFolderCreate 推送：
    /// 调用 GetFolderAsync → 解密 → UpsertFolder，VaultChanged 触发。
    /// </summary>
    [Fact]
    public async Task FolderCreate_Push_UpsertsFolderAndRaisesVaultChanged()
    {
        // Arrange
        var dto = MakeEncryptedFolderDto("f-1", "Work");
        var readApi = new FakeNotificationReadonlyApiClient { GetFolderResult = dto };
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session, readApi: readApi);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncFolderCreate, "f-1"),
            CancellationToken.None);

        // Assert — GetFolderAsync 被调用
        Assert.Equal("f-1", readApi.LastGetFolderId);

        // Assert — 文件夹出现在 session
        var folder = Assert.Single(session.Folders, f => f.Id == "f-1");
        Assert.Equal("Work", folder.Name);

        // Assert — VaultChanged 触发
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncFolderUpdate 推送：替换已存在文件夹的名称，VaultChanged 触发。
    /// </summary>
    [Fact]
    public async Task FolderUpdate_Push_ReplacesExistingFolderAndRaisesVaultChanged()
    {
        // Arrange
        var session = NewUnlockedSession();
        session.UpsertFolder(new Folder { Id = "f-1", Name = "Old Folder" });

        var dto = MakeEncryptedFolderDto("f-1", "Renamed Folder");
        var readApi = new FakeNotificationReadonlyApiClient { GetFolderResult = dto };
        var dispatcher = NewDispatcher(session, readApi: readApi);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncFolderUpdate, "f-1"),
            CancellationToken.None);

        // Assert — 文件夹数量不变
        Assert.Equal(1, session.Folders.Count);
        var folder = Assert.Single(session.Folders, f => f.Id == "f-1");
        Assert.Equal("Renamed Folder", folder.Name);

        // Assert — 事件触发
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// SyncFolderDelete 推送：
    /// 从 session.Folders 中移除指定 Id，VaultChanged 触发，无 GET 请求。
    /// </summary>
    [Fact]
    public async Task FolderDelete_Push_RemovesFolderAndRaisesVaultChanged()
    {
        // Arrange
        var session = NewUnlockedSession();
        session.UpsertFolder(new Folder { Id = "f-del", Name = "Bye" });

        var readApi = new FakeNotificationReadonlyApiClient();
        var dispatcher = NewDispatcher(session, readApi: readApi);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncFolderDelete, "f-del"),
            CancellationToken.None);

        // Assert — 不应调用 GetFolderAsync
        Assert.Empty(readApi.Calls);

        // Assert — 文件夹已移除
        Assert.Empty(session.Folders);

        // Assert — VaultChanged 触发
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
        // Arrange
        var cipherApi = new FakeAttachmentApiClient();
        var readApi = new FakeNotificationReadonlyApiClient();
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session, cipherApi: cipherApi, readApi: readApi);

        var sendsChangedCount = 0;
        var vaultChangedCount = 0;
        dispatcher.SendsChanged += () => sendsChangedCount++;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage(type, "send-id"),
            CancellationToken.None);

        // Assert — 只触发 SendsChanged
        Assert.Equal(1, sendsChangedCount);
        Assert.Equal(0, vaultChangedCount);

        // Assert — 无任何 API 调用
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
    public async Task BulkSyncPush_CallsSyncAsyncAndRaisesVaultChanged(int type)
    {
        // Arrange
        var sync = new FakeNotificationSyncService();
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session, sync: sync);

        var vaultChangedCount = 0;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage(type, null),
            CancellationToken.None);

        // Assert — SyncAsync 被调用一次
        Assert.Equal(1, sync.SyncCalls);

        // Assert — VaultChanged 触发
        Assert.Equal(1, vaultChangedCount);
    }

    /// <summary>
    /// AuthRequest 推送：仅触发 AuthRequestsChanged，不触发其他事件。
    /// </summary>
    [Fact]
    public async Task AuthRequest_Push_RaisesAuthRequestsChangedOnly()
    {
        // Arrange
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session);

        var authChangedCount = 0;
        var vaultChangedCount = 0;
        dispatcher.AuthRequestsChanged += () => authChangedCount++;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.AuthRequest, null),
            CancellationToken.None);

        // Assert
        Assert.Equal(1, authChangedCount);
        Assert.Equal(0, vaultChangedCount);
    }

    /// <summary>
    /// LogOut 推送（已解锁状态）：触发 LoggedOut 事件，不触发其他事件。
    /// </summary>
    [Fact]
    public async Task LogOut_Push_WhenUnlocked_RaisesLoggedOut()
    {
        // Arrange
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session);

        var loggedOutCount = 0;
        var vaultChangedCount = 0;
        dispatcher.LoggedOut += () => loggedOutCount++;
        dispatcher.VaultChanged += () => vaultChangedCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.LogOut, null),
            CancellationToken.None);

        // Assert
        Assert.Equal(1, loggedOutCount);
        Assert.Equal(0, vaultChangedCount);
    }

    /// <summary>
    /// LogOut 推送（已锁定状态，UserKey 为 null）：
    /// 即使 session 已锁定，仍必须触发 LoggedOut 事件（例外规则）。
    /// </summary>
    [Fact]
    public async Task LogOut_Push_WhenLocked_StillRaisesLoggedOut()
    {
        // Arrange — 锁定状态（UserKey = null）
        var session = NewLockedSession();
        var dispatcher = NewDispatcher(session);

        var loggedOutCount = 0;
        dispatcher.LoggedOut += () => loggedOutCount++;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.LogOut, null),
            CancellationToken.None);

        // Assert — LoggedOut 仍然触发
        Assert.Equal(1, loggedOutCount);
    }

    /// <summary>
    /// 锁定状态（UserKey = null）下的 SyncCipherCreate 推送：
    /// 期望完全忽略，不调用 API，不触发任何事件。
    /// </summary>
    [Fact]
    public async Task CipherCreate_WhenLocked_IsNoOpNoEvent()
    {
        // Arrange — 锁定状态
        var cipherApi = new FakeAttachmentApiClient();
        var session = NewLockedSession();
        var dispatcher = NewDispatcher(session, cipherApi: cipherApi);

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;
        dispatcher.SendsChanged += () => eventFired = true;
        dispatcher.AuthRequestsChanged += () => eventFired = true;
        dispatcher.LoggedOut += () => eventFired = true;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncCipherCreate, "c-locked"),
            CancellationToken.None);

        // Assert — 无 API 调用，无事件
        Assert.Empty(cipherApi.Calls);
        Assert.False(eventFired);
    }

    /// <summary>
    /// SyncCipherCreate 推送但 EntityId 为 null：应忽略，不调用 API，不触发事件。
    /// </summary>
    [Fact]
    public async Task CipherCreate_NullEntityId_IsNoOp()
    {
        // Arrange
        var cipherApi = new FakeAttachmentApiClient();
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session, cipherApi: cipherApi);

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage((int)UpdateType.SyncCipherCreate, null),
            CancellationToken.None);

        // Assert
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
        // Arrange
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session);

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;
        dispatcher.SendsChanged += () => eventFired = true;
        dispatcher.AuthRequestsChanged += () => eventFired = true;
        dispatcher.LoggedOut += () => eventFired = true;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage(type, "some-id"),
            CancellationToken.None);

        // Assert
        Assert.False(eventFired);
    }

    /// <summary>
    /// SyncFolderCreate/Update/Delete 推送但 EntityId 为 null：
    /// 应完全忽略——不调用 GetFolderAsync，不触发任何事件。
    /// 对称覆盖 CipherCreate_NullEntityId_IsNoOp。
    /// </summary>
    [Theory]
    [InlineData((int)UpdateType.SyncFolderCreate)]
    [InlineData((int)UpdateType.SyncFolderUpdate)]
    [InlineData((int)UpdateType.SyncFolderDelete)]
    public async Task FolderPush_NullEntityId_IsNoOp(int type)
    {
        // Arrange
        var readApi = new FakeNotificationReadonlyApiClient();
        var session = NewUnlockedSession();
        var dispatcher = NewDispatcher(session, readApi: readApi);

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage(type, null),
            CancellationToken.None);

        // Assert — 不应调用 GetFolderAsync
        Assert.Empty(readApi.Calls);

        // Assert — 无任何事件
        Assert.False(eventFired);
    }

    /// <summary>
    /// 锁定状态（UserKey = null）下的 SyncFolderCreate、SyncSendCreate、SyncCiphers 推送：
    /// 期望完全忽略——不调用任何 API，不触发任何事件。
    /// 对称覆盖现有的 CipherCreate_WhenLocked_IsNoOpNoEvent。
    /// </summary>
    [Theory]
    [InlineData((int)UpdateType.SyncFolderCreate)]
    [InlineData((int)UpdateType.SyncSendCreate)]
    [InlineData((int)UpdateType.SyncCiphers)]
    public async Task Push_WhenLocked_IsNoOpNoEvent(int type)
    {
        // Arrange — 锁定状态（UserKey = null）
        var cipherApi = new FakeAttachmentApiClient();
        var readApi = new FakeNotificationReadonlyApiClient();
        var sync = new FakeNotificationSyncService();
        var session = NewLockedSession();
        var dispatcher = NewDispatcher(session, cipherApi: cipherApi, readApi: readApi, sync: sync);

        var eventFired = false;
        dispatcher.VaultChanged += () => eventFired = true;
        dispatcher.SendsChanged += () => eventFired = true;
        dispatcher.AuthRequestsChanged += () => eventFired = true;
        dispatcher.LoggedOut += () => eventFired = true;

        // Act
        await dispatcher.DispatchAsync(
            new NotificationMessage(type, "some-id"),
            CancellationToken.None);

        // Assert — 无任何 API 调用
        Assert.Empty(cipherApi.Calls);
        Assert.Empty(readApi.Calls);
        Assert.Equal(0, sync.SyncCalls);

        // Assert — 无任何事件
        Assert.False(eventFired);
    }
}

// ─────────────────────────── 测试专用 Fake 类 ───────────────────────────

/// <summary>
/// IReadonlyApiClient 的最小化测试替身，只实现 GetFolderAsync / GetSendAsync。
/// 其余方法抛出 NotImplementedException（本测试中不会被调用）。
/// </summary>
public sealed class FakeNotificationReadonlyApiClient : IReadonlyApiClient
{
    public List<string> Calls { get; } = new();
    public string? LastGetFolderId { get; private set; }
    public FolderDto GetFolderResult { get; set; } = null!;

    public void SetBaseAddress(string baseUrl) { }

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
    public Task<Api.Dtos.TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.SyncResponse> GetSyncAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.ListResponse<Api.Dtos.DeviceDto>> GetDevicesAsync(CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Api.Dtos.SendResponseDto> GetSendAsync(string sendId, CancellationToken ct = default)
        => throw new NotImplementedException();
}

/// <summary>
/// ISyncService 的最小化测试替身，计数 SyncAsync 调用次数。
/// </summary>
public sealed class FakeNotificationSyncService : Core.Services.ISyncService
{
    public int SyncCalls { get; private set; }

    public Task<IReadOnlyList<Core.Models.Cipher>> SyncAsync(CancellationToken ct = default)
    {
        SyncCalls++;
        return Task.FromResult<IReadOnlyList<Core.Models.Cipher>>(Array.Empty<Core.Models.Cipher>());
    }
}
