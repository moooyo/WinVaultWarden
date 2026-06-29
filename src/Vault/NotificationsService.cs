using Api;
using Core.Abstractions;
using Core.Models;
using Core.Services;

namespace Vault;

/// <summary>
/// WebSocket 通知服务实现。
///
/// 职责：
/// 1. 建立并维持与 Vaultwarden /notifications/hub 的连接（通过 <see cref="INotificationsConnection"/> 工厂注入）。
/// 2. 将服务端推送转发给 <see cref="INotificationDispatcher"/>，再将 dispatcher 的事件透传为本服务的事件。
/// 3. 连接断开或异常时，以指数退避（base→cap）自动重连；每次重连前先尝试刷新 access token。
/// 4. <see cref="StopAsync"/> 取消后台循环，幂等。
///
/// 设计原则：
/// - StartAsync 立即返回（不阻塞），循环在后台 Task.Run 中运行。
/// - 连接失败仅触发退避重连，不抛出到调用方。
/// - serverUrl 优先从 <see cref="VaultSession.Account"/> 取，否则回落到 <see cref="ITokenStore"/>。
/// - accessToken 取自 <see cref="VaultSession.AccessToken"/>；若为空则无法连接，循环退出。
/// </summary>
public sealed class NotificationsService : INotificationsService
{
    private readonly Func<INotificationsConnection> _connectionFactory;
    private readonly INotificationDispatcher _dispatcher;
    private readonly VaultSession _session;
    private readonly ITokenStore _tokenStore;
    private readonly ITokenRefresher _refresher;
    private readonly TimeSpan _baseBackoff;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly object _startLock = new();

    public event Action? VaultChanged;
    public event Action? SendsChanged;
    public event Action? AuthRequestsChanged;
    public event Action? LoggedOut;

    /// <param name="connectionFactory">每次重连时调用，返回新的 <see cref="INotificationsConnection"/> 实例。</param>
    /// <param name="dispatcher">消息路由器，其事件会被透传为本服务的事件。</param>
    /// <param name="session">当前会话，提供 AccessToken 和 Account.ServerUrl。</param>
    /// <param name="tokenStore">持久化 token 存储，ServerUrl 回落来源。</param>
    /// <param name="refresher">token 刷新器，每次重连前尝试刷新。</param>
    /// <param name="baseBackoff">退避基准时长（测试可注入近零值）；默认 1 秒。</param>
    public NotificationsService(
        Func<INotificationsConnection> connectionFactory,
        INotificationDispatcher dispatcher,
        VaultSession session,
        ITokenStore tokenStore,
        ITokenRefresher refresher,
        TimeSpan? baseBackoff = null)
    {
        _connectionFactory = connectionFactory;
        _dispatcher        = dispatcher;
        _session           = session;
        _tokenStore        = tokenStore;
        _refresher         = refresher;
        _baseBackoff       = baseBackoff ?? TimeSpan.FromSeconds(1);

        // 透传 dispatcher 事件 → 本服务事件
        _dispatcher.VaultChanged        += () => VaultChanged?.Invoke();
        _dispatcher.SendsChanged        += () => SendsChanged?.Invoke();
        _dispatcher.AuthRequestsChanged += () => AuthRequestsChanged?.Invoke();
        _dispatcher.LoggedOut           += () => LoggedOut?.Invoke();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 幂等：若已运行则直接返回 <see cref="Task.CompletedTask"/>，不重复启动。
    /// </remarks>
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_startLock)
        {
            if (_loopTask is { IsCompleted: false })
                return Task.CompletedTask; // 已在运行

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            _loopTask = Task.Run(() => RunLoopAsync(token), token);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>幂等：若未运行则忽略。</remarks>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loopTask;

        lock (_startLock)
        {
            cts      = _cts;
            loopTask = _loopTask;
            _cts     = null;
            _loopTask = null;
        }

        if (cts is null)
            return;

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort cancel
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch
            {
                // best-effort await（loop 内部已处理所有异常）
            }
        }

        cts.Dispose();
    }

    // ─────────────────────────── 内部循环 ───────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var backoff = _baseBackoff;

        while (!ct.IsCancellationRequested)
        {
            // 1. 解析 serverUrl
            var serverUrl = ResolveServerUrl();
            if (string.IsNullOrEmpty(serverUrl))
                break; // 无法确定服务端地址，停止循环

            // 2. 解析 accessToken
            var accessToken = _session.AccessToken;
            if (string.IsNullOrEmpty(accessToken))
                break; // 无令牌，停止循环

            // 3. 建立连接并读取消息
            var conn = _connectionFactory();
            try
            {
                await conn.ConnectAsync(serverUrl, accessToken, ct).ConfigureAwait(false);
                backoff = _baseBackoff; // 重置退避

                await foreach (var msg in conn.ReadAsync(ct).ConfigureAwait(false))
                {
                    await _dispatcher.DispatchAsync(msg, ct).ConfigureAwait(false);
                }
                // ReadAsync 正常结束（连接关闭）→ 继续循环重连
            }
            catch (OperationCanceledException)
            {
                return; // 已取消，直接退出
            }
            catch
            {
                // 连接错误 → 退避后重连
            }
            finally
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
                return;

            // 4. 尝试刷新 token（best-effort）
            try
            {
                await _refresher.TryRefreshAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // best-effort
            }

            // 5. 退避等待后重连
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 指数退避，上限 MaxBackoff
            backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
        }
    }

    private string? ResolveServerUrl()
    {
        var urlFromSession = _session.Account.ServerUrl;
        if (!string.IsNullOrEmpty(urlFromSession))
            return urlFromSession;

        if (_tokenStore.TryLoad(out var persisted) && !string.IsNullOrEmpty(persisted.ServerUrl))
            return persisted.ServerUrl;

        return null;
    }
}
