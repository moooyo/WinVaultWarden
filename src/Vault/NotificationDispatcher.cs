using Api;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;

namespace Vault;

/// <summary>
/// 将 WebSocket 推送的 <see cref="NotificationMessage"/> 路由到对应的会话补丁或全量同步的服务接缝。
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>根据消息类型执行对应路由逻辑，全程绑定启动时捕获的 <paramref name="lease"/>。</summary>
    Task DispatchAsync(VaultSessionLease lease, NotificationMessage message, CancellationToken ct);

    /// <summary>任何密码库条目或文件夹被修改后触发（包含全量同步完成）。</summary>
    event Action? VaultChanged;

    /// <summary>Send 列表发生变化时触发。</summary>
    event Action? SendsChanged;

    /// <summary>收到 AuthRequest 推送时触发。</summary>
    event Action? AuthRequestsChanged;

    /// <summary>收到 LogOut 推送时触发（即使会话已锁定）。</summary>
    event Action? LoggedOut;

    /// <summary>Requests a connection rebuild after organization key membership changes.</summary>
    event Action? ConnectionRebuildRequested;
}

/// <summary>
/// 将 WebSocket 推送的 <see cref="NotificationMessage"/> 路由到对应的会话补丁或全量同步。
/// <para>
/// 路由逻辑：
/// <list type="bullet">
///   <item><see cref="UpdateType.SyncCipherCreate"/>/<see cref="UpdateType.SyncCipherUpdate"/>
///         → GET /api/ciphers/{id} → 解密 → <see cref="VaultSession.UpsertCipher"/> → <see cref="VaultChanged"/></item>
///   <item><see cref="UpdateType.SyncLoginDelete"/>
///         → <see cref="VaultSession.RemoveCipher"/> → <see cref="VaultChanged"/></item>
///   <item><see cref="UpdateType.SyncFolderCreate"/>/<see cref="UpdateType.SyncFolderUpdate"/>
///         → GET /api/folders/{id} → 解密 → <see cref="VaultSession.UpsertFolder"/> → <see cref="VaultChanged"/></item>
///   <item><see cref="UpdateType.SyncFolderDelete"/>
///         → <see cref="VaultSession.RemoveFolder"/> → <see cref="VaultChanged"/></item>
///   <item><see cref="UpdateType.SyncSendCreate"/>/<see cref="UpdateType.SyncSendUpdate"/>/<see cref="UpdateType.SyncSendDelete"/>
///         → <see cref="SendsChanged"/></item>
///   <item><see cref="UpdateType.SyncCiphers"/>/<see cref="UpdateType.SyncVault"/>/<see cref="UpdateType.SyncOrgKeys"/>/<see cref="UpdateType.SyncSettings"/>
///         → <see cref="ISyncService.SyncAsync"/> → <see cref="VaultChanged"/></item>
///   <item><see cref="UpdateType.AuthRequest"/> → <see cref="AuthRequestsChanged"/></item>
///   <item><see cref="UpdateType.LogOut"/> → <see cref="LoggedOut"/>（即使已锁定也触发）</item>
///   <item>其余类型（<see cref="UpdateType.None"/>/<see cref="UpdateType.AuthRequestResponse"/>及未知值）→ 忽略</item>
/// </list>
/// </para>
/// <para>
/// When the session no longer owns an active V1 or V2 user key, every message except
/// <see cref="UpdateType.LogOut"/> is ignored.
/// </para>
/// </summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IVaultApiClientProvider _clients;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;
    private readonly ISyncService _sync;
    private readonly ISessionTerminationSink? _termination;
    private readonly IDeviceIdentifierProvider? _deviceIdentifierProvider;
    private readonly ITokenStore? _tokenStore;
    private readonly VaultCacheService? _cache;

    public NotificationDispatcher(
        IVaultApiClientProvider clients,
        VaultDecryptor decryptor,
        VaultSession session,
        ISyncService sync,
        ISessionTerminationSink? termination = null,
        IDeviceIdentifierProvider? deviceIdentifierProvider = null,
        ITokenStore? tokenStore = null,
        VaultCacheService? cache = null)
    {
        _clients = clients;
        _decryptor = decryptor;
        _session = session;
        _sync = sync;
        _termination = termination;
        _deviceIdentifierProvider = deviceIdentifierProvider;
        _tokenStore = tokenStore;
        _cache = cache;
    }

    /// <summary>任何密码库条目或文件夹被修改后触发（包含全量同步完成）。</summary>
    public event Action? VaultChanged;

    /// <summary>Send 列表发生变化时触发。</summary>
    public event Action? SendsChanged;

    /// <summary>收到 AuthRequest 推送时触发。</summary>
    public event Action? AuthRequestsChanged;

    /// <summary>收到 LogOut 推送时触发（即使会话已锁定）。</summary>
    public event Action? LoggedOut;

    /// <inheritdoc />
    public event Action? ConnectionRebuildRequested;

    /// <summary>
    /// 根据 <paramref name="message"/> 的类型执行对应路由，全程绑定启动时捕获的 <paramref name="lease"/>：
    /// <list type="bullet">
    ///   <item>入口即校验 <see cref="VaultSession.IsCurrent"/>；stale（账户已切换/锁定/登出）直接 no-op。</item>
    ///   <item>补丁与事件永远在同一 <see cref="VaultSession.TryCommit"/> 内提交——校验与变更间无窗口；
    ///         TryCommit 返回 false（await 期间租约失效）时抛取消异常，绝不补丁或触发事件。</item>
    ///   <item>User-key routes take an owned V1/V2 copy with
    ///         <see cref="VaultSession.TryCopyUserKeyMaterial"/> before awaiting, then reject stale
    ///         responses through <see cref="VaultSession.ThrowIfExpired"/>.</item>
    ///   <item><see cref="UpdateType.LogOut"/> 是唯一不要求 UserKey 的分支（锁定后仍可登出），只需租约仍 current。</item>
    /// </list>
    /// </summary>
    public async Task DispatchAsync(VaultSessionLease lease, NotificationMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var type = (UpdateType)message.Type;

        if (!_session.IsCurrent(lease))
            return;
        if (IsCurrentContext(message.ContextId)
            || !MatchesAccount(message.UserId, lease.AccountId))
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);

        // LogOut is the only route that remains actionable while locked. Claim and invalidate the
        // exact lease first, then purge its persisted binding before publishing termination. This
        // prevents a same-account re-login race from deleting a newer generation. Application
        // lifecycle cleanup remains a second pass.
        if (type == UpdateType.LogOut)
        {
            VaultSessionLease? invalidatedLease = null;
            List<Exception>? persistenceFailures = null;
            if (_session.TryCommit(lease, () =>
                {
                    // VaultSession uses a re-entrant monitor. Holding its commit gate across the
                    // scoped purge prevents a same-account login from persisting a newer
                    // generation that this account-scoped removal could otherwise delete.
                    invalidatedLease = _session.Lock();
                    persistenceFailures = RemovePersistedSessionData(lease);
                }))
            {
                try
                {
                    _termination?.Request(new SessionTerminationNotice(
                        SessionTerminationReason.RemoteLogout,
                        invalidatedLease!));
                }
                finally
                {
                    try
                    {
                        LoggedOut?.Invoke();
                    }
                    finally
                    {
                        if (persistenceFailures is { Count: > 0 })
                        {
                            throw new AuthSessionPersistenceException(
                                "The remotely terminated session could not be removed securely.",
                                new AggregateException(persistenceFailures));
                        }
                    }
                }
            }
            return;
        }

        // 其余所有路由均要求解锁：TryCopyUserKey 原子校验"current + unlocked"，顺带取独立密钥拷贝
        // （调用方拥有其生命周期，锁定清零原 UserKey 不影响该拷贝）。stale 或已锁定时直接 no-op。
        if (!_session.TryCopyUserKeyMaterial(lease, out var copiedKey))
            return;
        using var key = copiedKey;

        switch (type)
        {
            case UpdateType.SyncCipherCreate:
            case UpdateType.SyncCipherUpdate:
            {
                if (message.EntityId is not { } id)
                    return;

                var api = _clients.GetAuthenticated<IAttachmentApiClient>(lease);
                var dto = await api.GetCipherAsync(id, linked.Token).ConfigureAwait(false);
                _session.ThrowIfExpired(lease);
                if (!string.IsNullOrWhiteSpace(dto.OrganizationId))
                {
                    await _sync.SyncAsync(lease, linked.Token).ConfigureAwait(false);
                    if (!_session.TryCommit(lease, () => VaultChanged?.Invoke()))
                        throw new OperationCanceledException(lease.CancellationToken);
                    return;
                }
                // DecryptCipher 内部已调用 ResolveItemKey，无需在外层重复调用。
                var cipher = key!.Legacy is { } legacy
                    ? _decryptor.DecryptCipher(dto, legacy)
                    : _decryptor.DecryptCipher(dto, key.V2!);
                if (!_session.TryCommit(lease, () =>
                {
                    _session.UpsertCipher(cipher);
                    VaultChanged?.Invoke();
                }))
                    throw new OperationCanceledException(lease.CancellationToken);
                return;
            }

            case UpdateType.SyncLoginDelete:
            case UpdateType.SyncCipherDelete:
            {
                if (message.EntityId is not { } id)
                    return;

                _session.TryCommit(lease, () =>
                {
                    _session.RemoveCipher(id);
                    VaultChanged?.Invoke();
                });
                return;
            }

            case UpdateType.SyncFolderCreate:
            case UpdateType.SyncFolderUpdate:
            {
                if (message.EntityId is not { } id)
                    return;

                var api = _clients.GetAuthenticated<IReadonlyApiClient>(lease);
                var dto = await api.GetFolderAsync(id, linked.Token).ConfigureAwait(false);
                _session.ThrowIfExpired(lease);
                var folder = key!.Legacy is { } legacy
                    ? _decryptor.DecryptFolder(dto, legacy)
                    : _decryptor.DecryptFolder(dto, key.V2!);
                if (!_session.TryCommit(lease, () =>
                {
                    _session.UpsertFolder(folder);
                    VaultChanged?.Invoke();
                }))
                    throw new OperationCanceledException(lease.CancellationToken);
                return;
            }

            case UpdateType.SyncFolderDelete:
            {
                if (message.EntityId is not { } id)
                    return;

                _session.TryCommit(lease, () =>
                {
                    _session.RemoveFolder(id);
                    _session.ClearFolderReference(id); // 引用被删文件夹的条目迁到"无文件夹"(R8)
                    VaultChanged?.Invoke();
                });
                return;
            }

            case UpdateType.SyncSendCreate:
            case UpdateType.SyncSendUpdate:
            case UpdateType.SyncSendDelete:
                _session.TryCommit(lease, () => SendsChanged?.Invoke());
                return;

            case UpdateType.SyncCiphers:
            case UpdateType.SyncVault:
            case UpdateType.SyncSettings:
            case UpdateType.SyncOrganizations:
            case UpdateType.SyncOrganizationStatusChanged:
            case UpdateType.SyncOrganizationCollectionSettingChanged:
            case UpdateType.SyncPolicy:
                await _sync.SyncAsync(lease, linked.Token).ConfigureAwait(false);
                if (!_session.TryCommit(lease, () => VaultChanged?.Invoke()))
                    throw new OperationCanceledException(lease.CancellationToken);
                return;

            case UpdateType.SyncOrgKeys:
                await _sync.SyncAsync(lease, linked.Token).ConfigureAwait(false);
                if (!_session.TryCommit(lease, () =>
                {
                    ConnectionRebuildRequested?.Invoke();
                    VaultChanged?.Invoke();
                }))
                    throw new OperationCanceledException(lease.CancellationToken);
                return;

            case UpdateType.AuthRequest:
                _session.TryCommit(lease, () => AuthRequestsChanged?.Invoke());
                return;

            // None、AuthRequestResponse 及所有未知类型 → 忽略
            default:
                return;
        }
    }

    private bool IsCurrentContext(string? contextId)
    {
        if (string.IsNullOrWhiteSpace(contextId) || _deviceIdentifierProvider is null)
            return false;
        try
        {
            return IdentifiersEqual(
                contextId,
                _deviceIdentifierProvider.GetDeviceIdentifier());
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
        {
            return false;
        }
    }

    private static bool MatchesAccount(string? payloadUserId, string accountId) =>
        payloadUserId is null || IdentifiersEqual(payloadUserId, accountId);

    private static bool IdentifiersEqual(string left, string right) =>
        Guid.TryParse(left, out var leftId) && Guid.TryParse(right, out var rightId)
            ? leftId == rightId
            : string.Equals(left, right, StringComparison.Ordinal);

    private List<Exception> RemovePersistedSessionData(VaultSessionLease lease)
    {
        var failures = new List<Exception>();
        if (_tokenStore is not null)
        {
            try
            {
                if (!_tokenStore.Remove(lease.AccountId, lease.Endpoints))
                {
                    failures.Add(new InvalidDataException(
                        "The persisted session could not be removed."));
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(exception);
            }
        }

        if (_cache is not null)
        {
            try
            {
                _cache.Clear(lease.AccountId, lease.Endpoints);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }
}
