using Api;
using Core.Enums;
using Core.Models;
using Core.Services;

namespace Vault;

/// <summary>
/// 将 WebSocket 推送的 <see cref="NotificationMessage"/> 路由到对应的会话补丁或全量同步的服务接缝。
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>根据消息类型执行对应路由逻辑。</summary>
    Task DispatchAsync(NotificationMessage msg, CancellationToken ct);

    /// <summary>任何密码库条目或文件夹被修改后触发（包含全量同步完成）。</summary>
    event Action? VaultChanged;

    /// <summary>Send 列表发生变化时触发。</summary>
    event Action? SendsChanged;

    /// <summary>收到 AuthRequest 推送时触发。</summary>
    event Action? AuthRequestsChanged;

    /// <summary>收到 LogOut 推送时触发（即使会话已锁定）。</summary>
    event Action? LoggedOut;
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
/// 若 <see cref="VaultSession.UserKey"/> 为 null（会话已锁定），
/// 除 <see cref="UpdateType.LogOut"/> 外所有消息均被忽略。
/// </para>
/// </summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IAttachmentApiClient _cipherApi;
    private readonly IReadonlyApiClient _readApi;
    private readonly VaultDecryptor _decryptor;
    private readonly VaultSession _session;
    private readonly ISyncService _sync;

    public NotificationDispatcher(
        IAttachmentApiClient cipherApi,
        IReadonlyApiClient readApi,
        VaultDecryptor decryptor,
        VaultSession session,
        ISyncService sync)
    {
        _cipherApi = cipherApi;
        _readApi = readApi;
        _decryptor = decryptor;
        _session = session;
        _sync = sync;
    }

    /// <summary>任何密码库条目或文件夹被修改后触发（包含全量同步完成）。</summary>
    public event Action? VaultChanged;

    /// <summary>Send 列表发生变化时触发。</summary>
    public event Action? SendsChanged;

    /// <summary>收到 AuthRequest 推送时触发。</summary>
    public event Action? AuthRequestsChanged;

    /// <summary>收到 LogOut 推送时触发（即使会话已锁定）。</summary>
    public event Action? LoggedOut;

    /// <summary>
    /// 根据 <paramref name="msg"/> 的 <see cref="NotificationMessage.Type"/> 执行对应路由逻辑。
    /// </summary>
    public async Task DispatchAsync(NotificationMessage msg, CancellationToken ct)
    {
        var type = (UpdateType)msg.Type;

        // LogOut 是唯一即使已锁定也要处理的类型。
        if (type == UpdateType.LogOut)
        {
            LoggedOut?.Invoke();
            return;
        }

        // 其余所有路由均需要解锁状态（UserKey 非空）。
        var userKey = _session.UserKey;
        if (userKey is null)
            return;

        switch (type)
        {
            case UpdateType.SyncCipherCreate:
            case UpdateType.SyncCipherUpdate:
            {
                var id = msg.EntityId;
                if (id is null)
                    return;

                var dto = await _cipherApi.GetCipherAsync(id, ct);
                // DecryptCipher 内部已调用 ResolveItemKey，无需在外层重复调用。
                var cipher = _decryptor.DecryptCipher(dto, userKey);
                _session.UpsertCipher(cipher);
                VaultChanged?.Invoke();
                break;
            }

            case UpdateType.SyncLoginDelete:
            {
                var id = msg.EntityId;
                if (id is null)
                    return;

                _session.RemoveCipher(id);
                VaultChanged?.Invoke();
                break;
            }

            case UpdateType.SyncFolderCreate:
            case UpdateType.SyncFolderUpdate:
            {
                var id = msg.EntityId;
                if (id is null)
                    return;

                var dto = await _readApi.GetFolderAsync(id, ct);
                var folder = _decryptor.DecryptFolder(dto, userKey);
                _session.UpsertFolder(folder);
                VaultChanged?.Invoke();
                break;
            }

            case UpdateType.SyncFolderDelete:
            {
                var id = msg.EntityId;
                if (id is null)
                    return;

                _session.RemoveFolder(id);
                VaultChanged?.Invoke();
                break;
            }

            case UpdateType.SyncSendCreate:
            case UpdateType.SyncSendUpdate:
            case UpdateType.SyncSendDelete:
                SendsChanged?.Invoke();
                break;

            case UpdateType.SyncCiphers:
            case UpdateType.SyncVault:
            case UpdateType.SyncOrgKeys:
            case UpdateType.SyncSettings:
                await _sync.SyncAsync(ct);
                VaultChanged?.Invoke();
                break;

            case UpdateType.AuthRequest:
                AuthRequestsChanged?.Invoke();
                break;

            // None、AuthRequestResponse 及所有未知类型 → 忽略
            default:
                break;
        }
    }
}
