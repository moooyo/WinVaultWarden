using Api;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Core.Session;

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
/// - StartAsync 只捕获一次租约（<see cref="VaultSession.CaptureLease"/>），整个循环——连接、
///   token 刷新、消息分发——始终复用该租约；绝不从 token store 或切换后的新账户重新解析 origin/token。
/// - The notifications endpoint comes from <see cref="VaultSessionLease.Endpoints"/>; the access token comes from
///   <see cref="VaultSession.GetAccessTokenBinding"/>（校验租约仍 current）。
/// - 连接失败仅触发退避重连，不抛出到调用方。
/// - 账户切换/锁定/登出会轮换 generation 并取消该租约的取消令牌，循环随之退出（旧连接不会看到新账户）。
/// </summary>
public sealed class NotificationsService : INotificationsService
{
    private readonly Func<INotificationsConnection> _connectionFactory;
    private readonly INotificationDispatcher _dispatcher;
    private readonly VaultSession _session;
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
    /// <param name="session">当前会话，提供启动时租约、绑定的 origin 与 access token。</param>
    /// <param name="refresher">token 刷新器，每次因错误断开后按启动租约尝试刷新。</param>
    /// <param name="baseBackoff">退避基准时长（测试可注入近零值）；默认 1 秒。</param>
    public NotificationsService(
        Func<INotificationsConnection> connectionFactory,
        INotificationDispatcher dispatcher,
        VaultSession session,
        ITokenRefresher refresher,
        TimeSpan? baseBackoff = null)
    {
        _connectionFactory = connectionFactory;
        _dispatcher        = dispatcher;
        _session           = session;
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

            // 只在启动时捕获一次租约；整个循环生命周期复用它。无活跃会话则无可监听，直接返回。
            VaultSessionLease lease;
            try
            {
                lease = _session.CaptureLease();
            }
            catch (InvalidOperationException)
            {
                return Task.CompletedTask;
            }

            // 循环取消令牌链接调用方 ct 与租约取消令牌：账户切换/锁定/登出会取消租约令牌，循环随之退出。
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
            var cts   = _cts;
            var token = cts.Token;
            _loopTask = Task.Run(async () =>
            {
                try
                {
                    await RunLoopAsync(lease, token).ConfigureAwait(false);
                }
                finally
                {
                    // caller 的 CancellationToken 直接取消时，StopAsync 未必被调用；
                    // 在此确保 linked CTS 得到 dispose，避免资源泄露。
                    // CancellationTokenSource.Dispose 是幂等的，StopAsync 也调用 dispose 无妨。
                    cts.Dispose();
                }
            }, token);
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

    private async Task RunLoopAsync(VaultSessionLease lease, CancellationToken ct)
    {
        var backoff = _baseBackoff;

        while (!ct.IsCancellationRequested && _session.IsCurrent(lease))
        {
            // 1. 从启动租约取绑定的 access token（校验租约仍 current）；为空则无法连接，停止循环。
            var binding = _session.GetAccessTokenBinding(lease);
            if (binding is null)
                return;

            // 2. Connect to the notifications endpoint frozen into the lease.
            var conn = _connectionFactory();
            var connectionFailed = false;
            try
            {
                await conn.ConnectAsync(
                    lease.Endpoints.Notifications.Value,
                    binding.Token,
                    ct).ConfigureAwait(false);
                backoff = _baseBackoff; // 重置退避

                await foreach (var msg in conn.ReadAsync(ct).ConfigureAwait(false))
                {
                    await _dispatcher.DispatchAsync(lease, msg, ct).ConfigureAwait(false);
                }
                // ReadAsync 正常结束（连接关闭）→ token 仍有效，继续循环重连
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // 已取消（含账户切换取消租约），直接退出
            }
            catch
            {
                connectionFailed = true; // 连接错误（可能是 token 失效）→ 退避后重连
            }
            finally
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
                return;

            // 3. 仅在连接因错误断开时才刷新 token（可能是 token 失效）。正常关闭无需刷新——token 仍有效，
            //    否则每次良性重连都多一次 connect/token 调用 + session.bin 落盘（R15）。若 token 确已过期，
            //    重连时 ConnectAsync 会失败 → 下一轮 connectionFailed=true 触发刷新，逻辑自愈。
            //    刷新绑定启动租约：陈旧租约（账户已切换）绝不轮换新账户的 token。
            if (connectionFailed)
            {
                try
                {
                    await _refresher.TryRefreshAsync(lease, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // best-effort
                }
            }

            // 4. 退避等待后重连
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
}
