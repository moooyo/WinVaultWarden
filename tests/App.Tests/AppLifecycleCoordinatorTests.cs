using App.Services;
using Xunit;

namespace App.Tests;

public sealed class AppLifecycleCoordinatorTests
{
    [Fact]
    public async Task Lock_RunsSecurityStepsInFixedOrder()
    {
        var log = new List<string>();
        var coordinator = Build(log);

        await coordinator.RequestAsync(AppLifecycleIntent.Lock);

        Assert.Equal([
            "block", "invalidate", "stop", "transients", "clipboard",
            "sensitive", "frames", "lock", "login"
        ], log);
    }

    [Fact]
    public async Task Logout_RunsSecurityStepsInFixedOrder()
    {
        var log = new List<string>();
        var coordinator = Build(log);

        await coordinator.RequestAsync(AppLifecycleIntent.Logout);

        Assert.Equal([
            "block", "invalidate", "stop", "transients", "clipboard",
            "sensitive", "frames", "logout", "login"
        ], log);
    }

    [Fact]
    public async Task Exit_RunsSecurityStepsThenExits_WithoutRunningLockOrLogoutSessionAction()
    {
        var log = new List<string>();
        var coordinator = Build(log);

        await coordinator.RequestAsync(AppLifecycleIntent.Exit);

        Assert.Equal([
            "block", "invalidate", "stop", "transients", "clipboard",
            "sensitive", "frames", "exit"
        ], log);
        Assert.DoesNotContain("lock", log);
        Assert.DoesNotContain("logout", log);
        Assert.DoesNotContain("login", log);
    }

    [Fact]
    public async Task ConcurrentLockAndExit_ShareTransitionAndUpgradeToExit()
    {
        var log = new List<string>();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Build(log, () => stop.Task);

        var first = coordinator.RequestAsync(AppLifecycleIntent.Lock);
        var second = coordinator.RequestAsync(AppLifecycleIntent.Exit);
        Assert.Same(first, second);

        stop.SetResult();
        await first;

        Assert.Equal(1, log.Count(value => value == "block"));
        Assert.Contains("exit", log);
        // 升级到 Exit 后不得再执行 Lock 意图的会话动作,也不得丢失更强 intent。
        Assert.DoesNotContain("lock", log);
        Assert.DoesNotContain("logout", log);
        Assert.DoesNotContain("login", log);
    }

    [Fact]
    public async Task ConcurrentLockAndLogout_ShareTransitionAndUpgradeToLogoutSessionAction()
    {
        var log = new List<string>();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Build(log, () => stop.Task);

        var first = coordinator.RequestAsync(AppLifecycleIntent.Lock);
        var second = coordinator.RequestAsync(AppLifecycleIntent.RemoteLogout);
        Assert.Same(first, second);

        stop.SetResult();
        await first;

        Assert.Equal(1, log.Count(value => value == "block"));
        Assert.Contains("logout", log);
        Assert.Contains("login", log);
        Assert.DoesNotContain("lock", log);
        Assert.DoesNotContain("exit", log);
    }

    [Fact]
    public async Task ConcurrentExitAndLock_NeverDowngradesAlreadySharedExitIntent()
    {
        var log = new List<string>();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Build(log, () => stop.Task);

        // Exit 请求先到,随后到达的较弱 Lock 请求绝不能使其降级。
        var first = coordinator.RequestAsync(AppLifecycleIntent.Exit);
        var second = coordinator.RequestAsync(AppLifecycleIntent.Lock);
        Assert.Same(first, second);

        stop.SetResult();
        await first;

        Assert.Contains("exit", log);
        Assert.DoesNotContain("lock", log);
        Assert.DoesNotContain("login", log);
    }

    [Fact]
    public async Task ThrowingStep_DoesNotSkipLaterSecuritySteps()
    {
        var log = new List<string>();
        var coordinator = Build(log, () => throw new InvalidOperationException("stop failed"));

        await coordinator.RequestAsync(AppLifecycleIntent.Logout);

        Assert.Contains("transients", log);
        Assert.Contains("clipboard", log);
        Assert.Contains("sensitive", log);
        Assert.Contains("frames", log);
        Assert.Contains("logout", log);
        Assert.Contains("login", log);
    }

    [Fact]
    public async Task ThrowingStep_IsReportedWithStepNameAndException()
    {
        var reports = new List<(string Step, Exception Ex)>();
        var log = new List<string>();
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => log.Add("block"),
            AllowSensitiveOperations: () => log.Add("allow"),
            InvalidateSession: () => log.Add("invalidate"),
            StopBackgroundAsync: () => throw new InvalidOperationException("stop failed"),
            CloseTransientsAsync: () => AddAsync(log, "transients"),
            CloseTransientsSynchronously: () => log.Add("transients-sync"),
            ClearClipboardAsync: () => AddAsync(log, "clipboard"),
            ClearClipboardSynchronously: () => log.Add("clipboard-sync"),
            ClearSensitiveState: () => log.Add("sensitive"),
            ResetFrames: () => log.Add("frames"),
            LockSessionAsync: () => AddAsync(log, "lock"),
            LogoutSessionAsync: () => AddAsync(log, "logout"),
            ShowLogin: () => log.Add("login"),
            ExitProcess: () => log.Add("exit"),
            FailClosedSession: () => log.Add("session-sync"),
            ReportFailure: (step, ex) => reports.Add((step, ex))));

        await coordinator.RequestAsync(AppLifecycleIntent.Lock);

        var report = Assert.Single(reports);
        Assert.Equal("stop", report.Step);
        Assert.IsType<InvalidOperationException>(report.Ex);
    }

    [Fact]
    public void FailClosedSynchronously_InvalidatesAndClearsWithoutAwait()
    {
        var log = new List<string>();
        var coordinator = Build(log);

        coordinator.FailClosedSynchronously();

        Assert.Equal([
            "block", "invalidate", "transients-sync", "clipboard-sync",
            "sensitive", "frames", "session-sync"
        ], log);
    }

    [Fact]
    public void MarkSessionReady_AllowsSensitiveOperations_WhenNotTransitioning()
    {
        var log = new List<string>();
        var coordinator = Build(log);

        coordinator.MarkSessionReady();

        Assert.Equal(["allow"], log);
        Assert.False(coordinator.IsTransitioning);
    }

    [Fact]
    public async Task MarkSessionReady_DoesNothing_WhileTransitionInProgress()
    {
        var log = new List<string>();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Build(log, () => stop.Task);

        var transition = coordinator.RequestAsync(AppLifecycleIntent.Lock);
        Assert.True(coordinator.IsTransitioning);

        coordinator.MarkSessionReady();
        Assert.DoesNotContain("allow", log);

        stop.SetResult();
        await transition;
    }

    private static AppLifecycleCoordinator Build(
        List<string> log,
        Func<Task>? stop = null) =>
        new(new AppLifecycleHooks(
            BlockSensitiveOperations: () => log.Add("block"),
            AllowSensitiveOperations: () => log.Add("allow"),
            InvalidateSession: () => log.Add("invalidate"),
            StopBackgroundAsync: stop ?? (() => AddAsync(log, "stop")),
            CloseTransientsAsync: () => AddAsync(log, "transients"),
            CloseTransientsSynchronously: () => log.Add("transients-sync"),
            ClearClipboardAsync: () => AddAsync(log, "clipboard"),
            ClearClipboardSynchronously: () => log.Add("clipboard-sync"),
            ClearSensitiveState: () => log.Add("sensitive"),
            ResetFrames: () => log.Add("frames"),
            LockSessionAsync: () => AddAsync(log, "lock"),
            LogoutSessionAsync: () => AddAsync(log, "logout"),
            ShowLogin: () => log.Add("login"),
            ExitProcess: () => log.Add("exit"),
            FailClosedSession: () => log.Add("session-sync"),
            ReportFailure: (_, _) => { }));

    private static Task AddAsync(List<string> log, string value)
    {
        log.Add(value);
        return Task.CompletedTask;
    }
}
