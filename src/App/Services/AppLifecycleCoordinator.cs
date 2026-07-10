namespace App.Services;

public enum AppLifecycleIntent
{
    Lock,
    Logout,
    CredentialChanged,
    RemoteLogout,
    Exit,
}

public interface IAppLifecycleCoordinator
{
    bool IsTransitioning { get; }
    Task RequestAsync(AppLifecycleIntent intent);
    void MarkSessionReady();
    void FailClosedSynchronously();
}

public sealed record AppLifecycleHooks(
    Action BlockSensitiveOperations,
    Action AllowSensitiveOperations,
    Action InvalidateSession,
    Func<Task> StopBackgroundAsync,
    Func<Task> CloseTransientsAsync,
    Action CloseTransientsSynchronously,
    Func<Task> ClearClipboardAsync,
    Action ClearClipboardSynchronously,
    Action ClearSensitiveState,
    Action ResetFrames,
    Func<Task> LockSessionAsync,
    Func<Task> LogoutSessionAsync,
    Action ShowLogin,
    Action ExitProcess,
    Action FailClosedSession,
    Action<string, Exception> ReportFailure);

/// <summary>
/// 全项目唯一的应用生命周期协调器(纯类型,不接 DI/WinUI)。
/// 固定顺序:block → invalidate → stop → transients → clipboard → sensitive → frames → session action → login/exit。
/// 调用者取消不能中止清理:一旦启动就会跑完全部步骤,任何单步异常都会被脱敏上报但不阻断后续步骤。
/// 并发请求共享同一个 transition;intent 只升级不降级(Exit > Logout/CredentialChanged/RemoteLogout > Lock),
/// 且在共享 transition 期间新到达的更强 intent 不会被丢弃(会在下一轮循环被捕获并处理)。
/// Exit 是终态意图,不需要 Lock/Logout 会话动作,清理完成后直接进程退出。
/// </summary>
public sealed class AppLifecycleCoordinator(AppLifecycleHooks hooks)
    : IAppLifecycleCoordinator
{
    private readonly object _gate = new();
    private Task? _transition;
    private AppLifecycleIntent _requested;
    private bool _hasRequest;

    public bool IsTransitioning
    {
        get { lock (_gate) return _transition is not null; }
    }

    public Task RequestAsync(AppLifecycleIntent intent)
    {
        lock (_gate)
        {
            if (!_hasRequest || Rank(intent) > Rank(_requested))
                _requested = intent;
            _hasRequest = true;
            return _transition ??= RunAsync();
        }
    }

    public void MarkSessionReady()
    {
        lock (_gate)
        {
            if (_transition is not null)
                return;
        }
        Safe("allow", hooks.AllowSensitiveOperations);
    }

    public void FailClosedSynchronously()
    {
        Safe("block", hooks.BlockSensitiveOperations);
        Safe("invalidate", hooks.InvalidateSession);
        Safe("transients-sync", hooks.CloseTransientsSynchronously);
        Safe("clipboard-sync", hooks.ClearClipboardSynchronously);
        Safe("sensitive", hooks.ClearSensitiveState);
        Safe("frames", hooks.ResetFrames);
        Safe("session-sync", hooks.FailClosedSession);
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        Safe("block", hooks.BlockSensitiveOperations);
        Safe("invalidate", hooks.InvalidateSession);
        await SafeAsync("stop", hooks.StopBackgroundAsync);
        await SafeAsync("transients", hooks.CloseTransientsAsync);
        await SafeAsync("clipboard", hooks.ClearClipboardAsync);
        Safe("sensitive", hooks.ClearSensitiveState);
        Safe("frames", hooks.ResetFrames);

        var completedRank = -1;
        while (true)
        {
            AppLifecycleIntent target;
            lock (_gate)
                target = _requested;

            var targetRank = Rank(target);
            if (targetRank > completedRank)
            {
                // Exit(rank 2) 是终态:没有对应的会话动作,清理完毕即退出进程。
                switch (targetRank)
                {
                    case 0:
                        await SafeAsync("lock", hooks.LockSessionAsync);
                        break;
                    case 1:
                        await SafeAsync("logout", hooks.LogoutSessionAsync);
                        break;
                }
                completedRank = targetRank;
                continue;
            }

            bool settled;
            lock (_gate)
            {
                settled = Rank(_requested) <= completedRank;
                if (settled)
                {
                    _transition = null;
                    _hasRequest = false;
                }
            }
            if (settled)
                break;
        }

        if (completedRank == 2)
            Safe("exit", hooks.ExitProcess);
        else
            Safe("login", hooks.ShowLogin);
    }

    private async Task SafeAsync(string step, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { hooks.ReportFailure(step, ex); }
    }

    private void Safe(string step, Action action)
    {
        try { action(); }
        catch (Exception ex) { hooks.ReportFailure(step, ex); }
    }

    private static int Rank(AppLifecycleIntent intent) => intent switch
    {
        AppLifecycleIntent.Exit => 2,
        AppLifecycleIntent.Logout or AppLifecycleIntent.CredentialChanged
            or AppLifecycleIntent.RemoteLogout => 1,
        _ => 0,
    };
}
