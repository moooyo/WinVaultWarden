using Core.Services;

namespace App.Services;

/// <summary>
/// App 层通知宿主：订阅 INotificationsService 的四个事件，
/// 通过外部注入的 Action 回调将事件桥接到 UI 线程（DispatcherQueue 由 App.xaml.cs 侧封装后传入）。
/// 保持纯 C# 可测试——不直接引用 DispatcherQueue 或任何 WinUI 类型。
/// </summary>
public sealed class NotificationsHost : IAsyncDisposable
{
    private readonly INotificationsService _service;

    /// <param name="service">INotificationsService 实现，由 DI 注入。</param>
    /// <param name="onVaultChanged">密码库变更回调（已由 App 侧 marshal 到 UI 线程）。</param>
    /// <param name="onSendsChanged">Send 变更回调。</param>
    /// <param name="onAuthRequestsChanged">Auth Request 变更回调。</param>
    /// <param name="onLoggedOut">服务端推送登出回调。</param>
    public NotificationsHost(
        INotificationsService service,
        Action onVaultChanged,
        Action onSendsChanged,
        Action onAuthRequestsChanged,
        Action onLoggedOut)
    {
        _service = service;

        _service.VaultChanged        += () => { try { onVaultChanged(); }        catch { } };
        _service.SendsChanged        += () => { try { onSendsChanged(); }        catch { } };
        _service.AuthRequestsChanged += () => { try { onAuthRequestsChanged(); } catch { } };
        _service.LoggedOut           += () => { try { onLoggedOut(); }           catch { } };
    }

    /// <summary>启动 WebSocket 推送连接（最佳努力，异常不冒泡）。</summary>
    public async Task StartAsync()
    {
        try
        {
            await _service.StartAsync();
        }
        catch
        {
            // 通知连接失败不应影响主流程
        }
    }

    /// <summary>停止 WebSocket 推送连接（最佳努力，异常不冒泡）。</summary>
    public async Task StopAsync()
    {
        try
        {
            await _service.StopAsync();
        }
        catch
        {
            // 同上
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync();
}
