namespace Core.Services;

/// <summary>
/// WebSocket 通知服务。负责建立/维持与 Vaultwarden /notifications/hub 的连接，
/// 将服务端推送的 UpdateType 消息分发为本地事件，供 ViewModel 订阅并触发增量同步。
/// </summary>
public interface INotificationsService
{
    /// <summary>建立 WebSocket 连接并开始监听推送消息。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>断开 WebSocket 连接并清理资源。</summary>
    Task StopAsync();

    /// <summary>密码库条目发生变更（新建/更新/删除）时触发。</summary>
    event Action? VaultChanged;

    /// <summary>Send 发生变更时触发。</summary>
    event Action? SendsChanged;

    /// <summary>Auth Request 发生变更时触发。</summary>
    event Action? AuthRequestsChanged;

    /// <summary>服务端推送 LogOut 时触发。</summary>
    event Action? LoggedOut;
}
