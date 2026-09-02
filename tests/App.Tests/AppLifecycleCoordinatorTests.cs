using App.Services;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class AppLifecycleCoordinatorTests
{
    [Fact]
    public void ConditionalRequest_IsRequiredInterfaceContract()
    {
        var method = typeof(IAppLifecycleCoordinator)
            .GetMethod(nameof(IAppLifecycleCoordinator.RequestIfAsync));

        Assert.NotNull(method);
        Assert.True(method!.IsAbstract);
    }

    [Fact]
    public async Task LockImmediatelyBlocksSensitiveStartsAndCompletesCleanup()
    {
        var invalidated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sensitiveCleared = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => { },
            AllowSensitiveOperations: () => { },
            InvalidateSession: invalidated.SetResult,
            StopBackgroundAsync: () => Task.CompletedTask,
            CloseTransientsAsync: () => Task.CompletedTask,
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => Task.CompletedTask,
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: sensitiveCleared.SetResult,
            ResetFrames: () => { },
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () => Task.CompletedTask,
            ShowLogin: () => { },
            ExitProcess: () => { },
            FailClosedSession: () => { },
            ReportFailure: (_, _) => { }));
        Assert.True(coordinator.MarkSessionReady());
        var ranBeforeLock = false;
        Assert.True(coordinator.TryRunSensitive(() =>
        {
            ranBeforeLock = true;
            return true;
        }));
        Assert.True(ranBeforeLock);

        var transition = coordinator.RequestAsync(AppLifecycleIntent.Lock);
        var ranAfterLock = false;
        Assert.False(coordinator.TryRunSensitive(() =>
        {
            ranAfterLock = true;
            return true;
        }));
        await transition.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(ranAfterLock);
        Assert.True(invalidated.Task.IsCompletedSuccessfully);
        Assert.True(sensitiveCleared.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void CoreLockBeforeLifecycleRequestRejectsSensitiveCommitStart()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => { },
            AllowSensitiveOperations: () => { },
            InvalidateSession: () => { },
            StopBackgroundAsync: () => Task.CompletedTask,
            CloseTransientsAsync: () => Task.CompletedTask,
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => Task.CompletedTask,
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => { },
            ResetFrames: () => { },
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () => Task.CompletedTask,
            ShowLogin: () => { },
            ExitProcess: () => { },
            FailClosedSession: () => { },
            ReportFailure: (_, _) => { }));
        Assert.True(coordinator.MarkSessionReady());
        session.Lock();
        var commitStarted = false;

        var accepted = coordinator.TryRunSensitive(
            () => session.TryRunIfUnlocked(
                lease,
                () => commitStarted = true));

        Assert.False(accepted);
        Assert.False(commitStarted);
    }

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
        Assert.True(coordinator.IsTransitioning);
    }

    [Fact]
    public async Task ExitCompletionPermanentlyRejectsActivationSensitiveWorkAndLowerRankRequests()
    {
        var log = new List<string>();
        var coordinator = Build(log);
        Assert.True(coordinator.MarkSessionReady());
        log.Clear();

        var exit = coordinator.RequestAsync(AppLifecycleIntent.Exit);
        await exit;

        var activationRan = false;
        var sensitiveRan = false;
        var predicateRan = false;
        Assert.True(coordinator.IsTransitioning);
        Assert.False(coordinator.TryActivateSession(() => activationRan = true));
        Assert.False(coordinator.TryRunSensitive(() =>
        {
            sensitiveRan = true;
            return true;
        }));
        var lowerRank = coordinator.RequestAsync(AppLifecycleIntent.Lock);
        var conditional = await coordinator.RequestIfAsync(
            AppLifecycleIntent.Logout,
            () =>
            {
                predicateRan = true;
                return true;
            });

        Assert.Same(exit, lowerRank);
        Assert.False(conditional);
        Assert.False(activationRan);
        Assert.False(sensitiveRan);
        Assert.False(predicateRan);
        Assert.Equal(1, log.Count(value => value == "exit"));
        Assert.DoesNotContain("allow", log);
        Assert.DoesNotContain("lock", log);
        Assert.DoesNotContain("logout", log);
        Assert.DoesNotContain("login", log);
    }

    [Fact]
    public async Task ThrowingExitHookIsReportedAndPermanentExitLatchRemainsClosed()
    {
        var reports = new List<(string Step, Exception Exception)>();
        var exitFailure = new InvalidOperationException("Injected exit failure.");
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => { },
            AllowSensitiveOperations: () => { },
            InvalidateSession: () => { },
            StopBackgroundAsync: () => Task.CompletedTask,
            CloseTransientsAsync: () => Task.CompletedTask,
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => Task.CompletedTask,
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => { },
            ResetFrames: () => { },
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () => Task.CompletedTask,
            ShowLogin: () => { },
            ExitProcess: () => throw exitFailure,
            FailClosedSession: () => { },
            ReportFailure: (step, exception) => reports.Add((step, exception))));

        var exit = coordinator.RequestAsync(AppLifecycleIntent.Exit);
        await exit;

        var report = Assert.Single(reports);
        Assert.Equal("exit", report.Step);
        Assert.Same(exitFailure, report.Exception);
        Assert.True(coordinator.IsTransitioning);
        Assert.False(coordinator.MarkSessionReady());
        Assert.False(coordinator.TryRunSensitive(static () => true));
        Assert.Same(exit, coordinator.RequestAsync(AppLifecycleIntent.Logout));
    }

    [Fact]
    public async Task ExitTimeoutsHangingAsyncCleanupThenRunsSynchronousFallbacksAndExit()
    {
        var log = new List<string>();
        var reports = new List<(string Step, Exception Exception)>();
        var never = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new AppLifecycleCoordinator(
            new AppLifecycleHooks(
                BlockSensitiveOperations: () => log.Add("block"),
                AllowSensitiveOperations: () => log.Add("allow"),
                InvalidateSession: () => log.Add("invalidate"),
                StopBackgroundAsync: () =>
                {
                    log.Add("stop-start");
                    return never.Task;
                },
                CloseTransientsAsync: () =>
                {
                    log.Add("transients-start");
                    return never.Task;
                },
                CloseTransientsSynchronously: () => log.Add("transients-sync"),
                ClearClipboardAsync: () =>
                {
                    log.Add("clipboard-start");
                    return never.Task;
                },
                ClearClipboardSynchronously: () => log.Add("clipboard-sync"),
                ClearSensitiveState: () => log.Add("sensitive"),
                ResetFrames: () => log.Add("frames"),
                LockSessionAsync: () => Task.CompletedTask,
                LogoutSessionAsync: () => Task.CompletedTask,
                ShowLogin: () => log.Add("login"),
                ExitProcess: () => log.Add("exit"),
                FailClosedSession: () => { },
                ReportFailure: (step, exception) => reports.Add((step, exception))),
            exitStepTimeout: TimeSpan.FromMilliseconds(20));

        var exit = coordinator.RequestAsync(AppLifecycleIntent.Exit);
        await exit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["stop", "transients", "clipboard"],
            reports.Select(report => report.Step));
        Assert.All(reports, report => Assert.IsType<TimeoutException>(report.Exception));
        Assert.Contains("transients-sync", log);
        Assert.Contains("clipboard-sync", log);
        Assert.True(log.IndexOf("sensitive") > log.IndexOf("clipboard-sync"));
        Assert.True(log.IndexOf("exit") > log.IndexOf("frames"));
        Assert.DoesNotContain("login", log);
        Assert.True(coordinator.IsTransitioning);
        Assert.False(coordinator.MarkSessionReady());
        Assert.False(coordinator.TryRunSensitive(static () => true));
    }

    [Fact]
    public async Task LockDoesNotApplyExitTimeoutToHangingCleanup()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var coordinator = Build(log, async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        var transition = coordinator.RequestAsync(AppLifecycleIntent.Lock);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);

        Assert.False(transition.IsCompleted);
        Assert.DoesNotContain("login", log);
        release.TrySetResult();
        await transition.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("login", log);
    }

    [Theory]
    [InlineData(AppLifecycleIntent.Lock, CleanupFailureMode.SynchronousThrow)]
    [InlineData(AppLifecycleIntent.Lock, CleanupFailureMode.NullTask)]
    [InlineData(AppLifecycleIntent.Lock, CleanupFailureMode.CompletedFault)]
    [InlineData(AppLifecycleIntent.Lock, CleanupFailureMode.AsynchronousFault)]
    [InlineData(AppLifecycleIntent.Exit, CleanupFailureMode.SynchronousThrow)]
    [InlineData(AppLifecycleIntent.Exit, CleanupFailureMode.NullTask)]
    [InlineData(AppLifecycleIntent.Exit, CleanupFailureMode.CompletedFault)]
    [InlineData(AppLifecycleIntent.Exit, CleanupFailureMode.AsynchronousFault)]
    public async Task FailedTransientAndClipboardCleanupAlwaysRunSynchronousFallbacks(
        AppLifecycleIntent intent,
        CleanupFailureMode failureMode)
    {
        var log = new List<string>();
        var reports = new List<(string Step, Exception Exception)>();
        var coordinator = new AppLifecycleCoordinator(
            new AppLifecycleHooks(
                BlockSensitiveOperations: () => log.Add("block"),
                AllowSensitiveOperations: () => log.Add("allow"),
                InvalidateSession: () => log.Add("invalidate"),
                StopBackgroundAsync: () => Task.CompletedTask,
                CloseTransientsAsync: () => FailedCleanup(failureMode),
                CloseTransientsSynchronously: () => log.Add("transients-sync"),
                ClearClipboardAsync: () => FailedCleanup(failureMode),
                ClearClipboardSynchronously: () => log.Add("clipboard-sync"),
                ClearSensitiveState: () => log.Add("sensitive"),
                ResetFrames: () => log.Add("frames"),
                LockSessionAsync: () => Task.CompletedTask,
                LogoutSessionAsync: () => Task.CompletedTask,
                ShowLogin: () => log.Add("login"),
                ExitProcess: () => log.Add("exit"),
                FailClosedSession: () => { },
                ReportFailure: (step, exception) => reports.Add((step, exception))),
            exitStepTimeout: TimeSpan.FromMilliseconds(100));

        await coordinator.RequestAsync(intent).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["transients", "clipboard"],
            reports.Select(report => report.Step));
        Assert.Contains("transients-sync", log);
        Assert.Contains("clipboard-sync", log);
        Assert.True(log.IndexOf("transients-sync") < log.IndexOf("clipboard-sync"));
        Assert.True(log.IndexOf("clipboard-sync") < log.IndexOf("sensitive"));
        if (intent == AppLifecycleIntent.Exit)
        {
            Assert.Contains("exit", log);
            Assert.DoesNotContain("login", log);
            Assert.True(coordinator.IsTransitioning);
        }
        else
        {
            Assert.Contains("login", log);
            Assert.DoesNotContain("exit", log);
            Assert.False(coordinator.IsTransitioning);
        }
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
        // Upgrading to Exit must skip the Lock session action and preserve the stronger intent.
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

        // A later Lock request must not downgrade an already claimed Exit.
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
    public async Task ThrowingStep_StillRunsRemainingCleanup_WhenReportFailureThrows()
    {
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
            ReportFailure: (_, _) => throw new InvalidOperationException("report failed")));

        await coordinator.RequestAsync(AppLifecycleIntent.Logout);

        Assert.Equal([
            "block", "invalidate", "transients", "clipboard",
            "sensitive", "frames", "logout", "login"
        ], log);
    }

    [Fact]
    public void FailClosedSynchronously_StillRunsRemainingCleanup_WhenReportFailureThrows()
    {
        var log = new List<string>();
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => log.Add("block"),
            AllowSensitiveOperations: () => log.Add("allow"),
            InvalidateSession: () => log.Add("invalidate"),
            StopBackgroundAsync: () => Task.CompletedTask,
            CloseTransientsAsync: () => Task.CompletedTask,
            CloseTransientsSynchronously: () => throw new InvalidOperationException("transients-sync failed"),
            ClearClipboardAsync: () => Task.CompletedTask,
            ClearClipboardSynchronously: () => log.Add("clipboard-sync"),
            ClearSensitiveState: () => log.Add("sensitive"),
            ResetFrames: () => log.Add("frames"),
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () => Task.CompletedTask,
            ShowLogin: () => log.Add("login"),
            ExitProcess: () => log.Add("exit"),
            FailClosedSession: () => log.Add("session-sync"),
            ReportFailure: (_, _) => throw new InvalidOperationException("report failed")));

        coordinator.FailClosedSynchronously();

        Assert.Equal([
            "block", "invalidate", "clipboard-sync",
            "sensitive", "frames", "session-sync"
        ], log);
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

    [Fact]
    public async Task RequestIf_FalsePredicateDoesNotClaimTransition()
    {
        var log = new List<string>();
        var coordinator = Build(log);
        Assert.True(coordinator.MarkSessionReady());
        log.Clear();

        var requested = await coordinator.RequestIfAsync(
            AppLifecycleIntent.Lock,
            () => false);

        Assert.False(requested);
        Assert.False(coordinator.IsTransitioning);
        Assert.Empty(log);
    }

    [Fact]
    public async Task RequestIf_ThrowingPredicateDoesNotClaimTransition()
    {
        var log = new List<string>();
        var coordinator = Build(log);
        Assert.True(coordinator.MarkSessionReady());
        log.Clear();

        var requested = await coordinator.RequestIfAsync(
            AppLifecycleIntent.Lock,
            () => throw new InvalidOperationException("predicate failed"));

        Assert.False(requested);
        Assert.False(coordinator.IsTransitioning);
        Assert.Empty(log);
    }

    [Fact]
    public async Task RequestIf_ClaimsTransitionBeforeConcurrentSessionActivation()
    {
        var log = new List<string>();
        var predicateEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePredicate = new ManualResetEventSlim();
        var stopEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = Build(log, () =>
        {
            stopEntered.TrySetResult();
            return releaseStop.Task;
        });
        Assert.True(coordinator.MarkSessionReady());

        var request = Task.Run(async () => await coordinator.RequestIfAsync(
            AppLifecycleIntent.Lock,
            () =>
            {
                predicateEntered.TrySetResult();
                releasePredicate.Wait();
                return true;
            }));
        await predicateEntered.Task;

        var activationRan = false;
        var activation = Task.Run(() => coordinator.TryActivateSession(() =>
        {
            activationRan = true;
            return true;
        }));
        releasePredicate.Set();
        await stopEntered.Task;

        Assert.False(await activation);
        Assert.False(activationRan);
        releaseStop.TrySetResult();
        Assert.True(await request);
    }

    [Fact]
    public async Task RequestIf_WhenSessionBActivatesFirst_DoesNotStartTransition()
    {
        var log = new List<string>();
        var coordinator = Build(log);
        Assert.True(coordinator.MarkSessionReady());
        var currentAccount = "A";
        Assert.True(coordinator.TryActivateSession(() =>
        {
            currentAccount = "B";
            return true;
        }));
        log.Clear();

        var requested = await coordinator.RequestIfAsync(
            AppLifecycleIntent.Lock,
            () => currentAccount == "A");

        Assert.False(requested);
        Assert.False(coordinator.IsTransitioning);
        Assert.Empty(log);
    }

    [Fact]
    public async Task ConditionalRemoteLogout_ClaimsSessionBeforeSlowCleanup()
    {
        var session = new VaultSession();
        var active = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        Assert.True(session.TryLockIfCurrent(active, out var invalidated));
        var log = new List<string>();

        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => log.Add("block"),
            AllowSensitiveOperations: () => log.Add("allow"),
            InvalidateSession: () => log.Add("invalidate"),
            StopBackgroundAsync: () => AddAsync(log, "stop"),
            CloseTransientsAsync: () => AddAsync(log, "transients"),
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => AddAsync(log, "clipboard"),
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => log.Add("sensitive"),
            ResetFrames: () => log.Add("frames"),
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () => AddAsync(log, "logout-unconditional"),
            ShowLogin: () => log.Add("login"),
            ExitProcess: () => { },
            FailClosedSession: () => { },
            ReportFailure: (_, _) => { },
            TryLogoutExpectedSessionAsync: expected =>
            {
                log.Add("claim");
                var committed = session.TryCommit(expected, session.Logout);
                return Task.FromResult(committed);
            }));

        await coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            invalidated);

        Assert.Equal("claim", log[1]);
        Assert.True(log.IndexOf("claim") < log.IndexOf("stop"));
        Assert.DoesNotContain("logout-unconditional", log);
        Assert.Equal(VaultState.LoggedOut, session.State);
    }

    [Fact]
    public async Task ConditionalRemoteLogout_WhenNewSessionPromotesBeforeClaim_AbortsCleanup()
    {
        var session = new VaultSession();
        var activeA = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        Assert.True(session.TryLockIfCurrent(activeA, out var invalidatedA));

        var attemptB = session.BeginAuthenticationAttempt();
        var claimEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => log.Add("block"),
            AllowSensitiveOperations: () => log.Add("allow"),
            InvalidateSession: () => log.Add("invalidate"),
            StopBackgroundAsync: () => AddAsync(log, "stop"),
            CloseTransientsAsync: () => AddAsync(log, "transients"),
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => AddAsync(log, "clipboard"),
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => log.Add("sensitive"),
            ResetFrames: () => log.Add("frames"),
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () => AddAsync(log, "logout-unconditional"),
            ShowLogin: () => log.Add("login"),
            ExitProcess: () => { },
            FailClosedSession: () => { },
            ReportFailure: (_, _) => { },
            TryLogoutExpectedSessionAsync: async expected =>
            {
                log.Add("claim");
                claimEntered.TrySetResult();
                await releaseClaim.Task;
                return session.TryCommit(expected, session.Logout);
            }));

        var transition = coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            invalidatedA);
        await claimEntered.Task;

        Assert.True(session.TryBeginAccountSession(
            attemptB,
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"),
            out var leaseB));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Repeat((byte)1, 64).ToArray()));
        releaseClaim.SetResult();
        await transition;

        Assert.True(session.IsUnlocked(leaseB!));
        Assert.Equal(["block", "claim", "allow"], log);
        Assert.False(coordinator.IsTransitioning);
    }

    [Fact]
    public async Task NonExitCompletionStillAllowsExplicitSessionRecovery()
    {
        var log = new List<string>();
        var coordinator = Build(log);

        await coordinator.RequestAsync(AppLifecycleIntent.Lock);

        Assert.False(coordinator.IsTransitioning);
        Assert.True(coordinator.MarkSessionReady());
        var sensitiveRan = false;
        Assert.True(coordinator.TryRunSensitive(() =>
        {
            sensitiveRan = true;
            return true;
        }));
        Assert.True(sensitiveRan);
        Assert.Equal("allow", log[^1]);
    }

    [Fact]
    public async Task ConditionalRemoteLogout_ClaimFault_FinishesFailClosedCleanup()
    {
        var log = new List<string>();
        var reports = new List<string>();
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => log.Add("block"),
            AllowSensitiveOperations: () => log.Add("allow"),
            InvalidateSession: () => log.Add("invalidate"),
            StopBackgroundAsync: () => AddAsync(log, "stop"),
            CloseTransientsAsync: () => AddAsync(log, "transients"),
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => AddAsync(log, "clipboard"),
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => log.Add("sensitive"),
            ResetFrames: () => log.Add("frames"),
            LockSessionAsync: () => AddAsync(log, "lock"),
            LogoutSessionAsync: () => AddAsync(log, "logout"),
            ShowLogin: () => log.Add("login"),
            ExitProcess: () => log.Add("exit"),
            FailClosedSession: () => { },
            ReportFailure: (step, _) => reports.Add(step),
            TryLogoutExpectedSessionAsync: _ =>
                throw new IOException("Persistent cleanup failed.")));

        await coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            TestSessionLeases.CreateSessionLease());

        Assert.Equal([
            "block", "invalidate", "stop", "transients", "clipboard",
            "sensitive", "frames", "logout", "login",
        ], log);
        Assert.Equal(["logout-expected"], reports);
        Assert.DoesNotContain("allow", log);
    }

    [Fact]
    public async Task ConcurrentConditionalLogout_ReplacesExpectedLeaseWithoutBecomingUnconditional()
    {
        var session = new VaultSession();
        var activeA = session.BeginAccountSession(
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        Assert.True(session.TryLockIfCurrent(activeA, out var invalidatedA));

        var activeB = session.BeginAccountSession(
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Repeat((byte)1, 64).ToArray()));
        Assert.True(session.TryLockIfCurrent(activeB, out var invalidatedB));

        var claimAEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaimA = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var claimBEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaimB = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed = new List<VaultSessionLease>();
        var unconditionalCalls = 0;
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => { },
            AllowSensitiveOperations: () => { },
            InvalidateSession: () =>
            {
                unconditionalCalls++;
                session.Logout();
            },
            StopBackgroundAsync: () => Task.CompletedTask,
            CloseTransientsAsync: () => Task.CompletedTask,
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => Task.CompletedTask,
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => { },
            ResetFrames: () => { },
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () =>
            {
                unconditionalCalls++;
                session.Logout();
                return Task.CompletedTask;
            },
            ShowLogin: () => { },
            ExitProcess: () => { },
            FailClosedSession: () => { },
            ReportFailure: (_, _) => { },
            TryLogoutExpectedSessionAsync: async expected =>
            {
                claimed.Add(expected);
                if (ReferenceEquals(expected, invalidatedA))
                {
                    claimAEntered.TrySetResult();
                    await releaseClaimA.Task;
                }
                else
                {
                    claimBEntered.TrySetResult();
                    await releaseClaimB.Task;
                }
                return session.TryCommit(expected, session.Logout);
            }));

        var first = coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            invalidatedA);
        await claimAEntered.Task;
        var second = coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            invalidatedB);
        Assert.Same(first, second);

        releaseClaimA.SetResult();
        await claimBEntered.Task;
        var activeC = session.BeginAccountSession(
            "account-c",
            "c@example.com",
            new ServerOrigin("https://c.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Repeat((byte)2, 64).ToArray()));
        releaseClaimB.SetResult();
        await first;

        Assert.Equal([invalidatedA!, invalidatedB!], claimed);
        Assert.Equal(0, unconditionalCalls);
        Assert.True(session.IsUnlocked(activeC));
    }

    [Fact]
    public async Task SuccessfulConditionalClaim_DoesNotSettleReplacementRequest()
    {
        var expectedA = TestSessionLeases.CreateSessionLease();
        var expectedB = TestSessionLeases.CreateSessionLease();
        var claimAEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaimA = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var claimed = new List<VaultSessionLease>();
        var cleanupCalls = 0;
        var unconditionalCalls = 0;
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => { },
            AllowSensitiveOperations: () => { },
            InvalidateSession: () => unconditionalCalls++,
            StopBackgroundAsync: () =>
            {
                cleanupCalls++;
                return Task.CompletedTask;
            },
            CloseTransientsAsync: () => Task.CompletedTask,
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => Task.CompletedTask,
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => { },
            ResetFrames: () => { },
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () =>
            {
                unconditionalCalls++;
                return Task.CompletedTask;
            },
            ShowLogin: () => { },
            ExitProcess: () => { },
            FailClosedSession: () => { },
            ReportFailure: (_, _) => { },
            TryLogoutExpectedSessionAsync: async expected =>
            {
                claimed.Add(expected);
                if (ReferenceEquals(expected, expectedA))
                {
                    claimAEntered.TrySetResult();
                    await releaseClaimA.Task;
                }
                return true;
            }));

        var first = coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            expectedA);
        await claimAEntered.Task;
        var second = coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            expectedB);
        Assert.Same(first, second);
        releaseClaimA.SetResult();
        await first;

        Assert.Equal([expectedA, expectedB], claimed);
        Assert.Equal(1, cleanupCalls);
        Assert.Equal(0, unconditionalCalls);
    }

    [Fact]
    public async Task SuccessfulConditionalClaim_DoesNotSettleUnconditionalUpgrade()
    {
        var expected = TestSessionLeases.CreateSessionLease();
        var claimEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClaim = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidations = 0;
        var unconditionalLogouts = 0;
        var coordinator = new AppLifecycleCoordinator(new AppLifecycleHooks(
            BlockSensitiveOperations: () => { },
            AllowSensitiveOperations: () => { },
            InvalidateSession: () => invalidations++,
            StopBackgroundAsync: () => Task.CompletedTask,
            CloseTransientsAsync: () => Task.CompletedTask,
            CloseTransientsSynchronously: () => { },
            ClearClipboardAsync: () => Task.CompletedTask,
            ClearClipboardSynchronously: () => { },
            ClearSensitiveState: () => { },
            ResetFrames: () => { },
            LockSessionAsync: () => Task.CompletedTask,
            LogoutSessionAsync: () =>
            {
                unconditionalLogouts++;
                return Task.CompletedTask;
            },
            ShowLogin: () => { },
            ExitProcess: () => { },
            FailClosedSession: () => { },
            ReportFailure: (_, _) => { },
            TryLogoutExpectedSessionAsync: async _ =>
            {
                claimEntered.TrySetResult();
                await releaseClaim.Task;
                return true;
            }));

        var first = coordinator.RequestAsync(
            AppLifecycleIntent.RemoteLogout,
            expected);
        await claimEntered.Task;
        var second = coordinator.RequestAsync(AppLifecycleIntent.RemoteLogout);
        Assert.Same(first, second);
        releaseClaim.SetResult();
        await first;

        Assert.Equal(1, invalidations);
        Assert.Equal(1, unconditionalLogouts);
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

    private static Task FailedCleanup(CleanupFailureMode mode) => mode switch
    {
        CleanupFailureMode.SynchronousThrow =>
            throw new InvalidOperationException("Injected synchronous cleanup failure."),
        CleanupFailureMode.NullTask => null!,
        CleanupFailureMode.CompletedFault => Task.FromException(
            new InvalidOperationException("Injected completed cleanup failure.")),
        CleanupFailureMode.AsynchronousFault => FailCleanupAsynchronously(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static async Task FailCleanupAsynchronously()
    {
        await Task.Yield();
        throw new InvalidOperationException("Injected asynchronous cleanup failure.");
    }

    public enum CleanupFailureMode
    {
        SynchronousThrow,
        NullTask,
        CompletedFault,
        AsynchronousFault,
    }
}
