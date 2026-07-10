using App.Services;
using Xunit;

namespace App.Tests;

public sealed class UiCoordinationPrimitivesTests
{
    [Fact]
    public void AsyncGate_OnlyOneCallerEntersUntilLeaseDisposed()
    {
        var gate = new AsyncOperationGate();
        Assert.True(gate.TryEnter(out var first));
        Assert.False(gate.TryEnter(out var rejected));
        Assert.Null(rejected);
        first!.Dispose();
        Assert.True(gate.TryEnter(out var second));
        second!.Dispose();
    }

    [Fact]
    public async Task AsyncGate_UnderConcurrentContention_NeverObservesTwoHoldersAtOnce()
    {
        var gate = new AsyncOperationGate();
        var concurrentHolders = 0;
        var maxObservedHolders = 0;
        var successfulEntries = 0;

        var workers = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                if (!gate.TryEnter(out var lease))
                    continue;

                Interlocked.Increment(ref successfulEntries);
                var current = Interlocked.Increment(ref concurrentHolders);
                InterlockedMax(ref maxObservedHolders, current);
                Interlocked.Decrement(ref concurrentHolders);
                lease!.Dispose();
            }
        }));

        await Task.WhenAll(workers);

        Assert.True(successfulEntries > 0);
        Assert.Equal(1, maxObservedHolders);
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int initial;
        do
        {
            initial = Volatile.Read(ref target);
            if (candidate <= initial)
                return;
        } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
    }

    [Fact]
    public void SensitiveRegistry_ClearsEveryLiveEntry_WhenOneThrows()
    {
        var calls = new List<string>();
        var registry = new SensitiveStateRegistry(_ => { });
        ISensitiveState[] states =
        [
            new State(() => calls.Add("one")),
            new State(() => throw new InvalidOperationException("boom")),
            new State(() => calls.Add("three")),
        ];
        foreach (var state in states)
            registry.Register(state);

        registry.ClearAll();

        Assert.Equal(["one", "three"], calls);
        GC.KeepAlive(states);
    }

    [Fact]
    public void SensitiveRegistry_ClearAll_ReportsThrownExceptionWithoutStoppingRemainingEntries()
    {
        Exception? reported = null;
        var registry = new SensitiveStateRegistry(ex => reported = ex);
        registry.Register(new State(() => throw new InvalidOperationException("boom")));
        var calls = new List<string>();
        registry.Register(new State(() => calls.Add("after")));

        registry.ClearAll();

        Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal(["after"], calls);
    }

    [Fact]
    public void SensitiveRegistry_ClearAll_StillRunsRemainingEntries_WhenReportFailureThrows()
    {
        var calls = new List<string>();
        var registry = new SensitiveStateRegistry(_ => throw new InvalidOperationException("report failed"));
        registry.Register(new State(() => throw new InvalidOperationException("boom")));
        registry.Register(new State(() => calls.Add("after")));

        registry.ClearAll();

        Assert.Equal(["after"], calls);
    }

    [Fact]
    public async Task SensitiveRegistry_ClearAll_DoesNotHoldInternalLockWhileInvokingCallbacks()
    {
        // 若 ClearAll 在持锁期间调用 ClearSensitive,另一线程的并发 Register 会被阻塞到回调结束。
        var registry = new SensitiveStateRegistry(_ => { });
        var callbackStarted = new ManualResetEventSlim();
        var releaseCallback = new ManualResetEventSlim();
        registry.Register(new State(() =>
        {
            callbackStarted.Set();
            releaseCallback.Wait(TimeSpan.FromSeconds(5));
        }));

        var clearTask = Task.Run(() => registry.ClearAll());
        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)), "回调应已开始执行");

        var registerTask = Task.Run(() => registry.Register(new State(() => { })));
        await registerTask.WaitAsync(TimeSpan.FromSeconds(5));

        releaseCallback.Set();
        await clearTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TransientRegistry_CancelsClosesAndClearsEveryEntry()
    {
        var calls = new List<string>();
        var registry = new TransientOperationRegistry(_ => { });
        using var registration = registry.Register(
            () => calls.Add("cancel"),
            () => calls.Add("close"),
            () => calls.Add("clear"));

        await registry.CancelAndCloseAllAsync();

        Assert.Equal(["cancel", "close", "clear"], calls);
    }

    [Fact]
    public async Task TransientRegistry_CancelAndCloseAll_ContinuesAndReportsWhenAStepThrows()
    {
        var calls = new List<string>();
        Exception? reported = null;
        var registry = new TransientOperationRegistry(ex => reported = ex);
        registry.Register(
            () => throw new InvalidOperationException("cancel failed"),
            () => calls.Add("close"),
            () => calls.Add("clear"));

        await registry.CancelAndCloseAllAsync();

        Assert.Equal(["close", "clear"], calls);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public async Task TransientRegistry_CancelAndCloseAll_StillRunsRemainingSteps_WhenReportFailureThrows()
    {
        var calls = new List<string>();
        var registry = new TransientOperationRegistry(_ => throw new InvalidOperationException("report failed"));
        registry.Register(
            () => throw new InvalidOperationException("cancel failed"),
            () => calls.Add("close"),
            () => calls.Add("clear"));

        await registry.CancelAndCloseAllAsync();

        Assert.Equal(["close", "clear"], calls);
    }

    [Fact]
    public void TransientRegistry_Dispose_RemovesRegistrationWithoutInvokingCallbacks_AndIsIdempotent()
    {
        var calls = new List<string>();
        var registry = new TransientOperationRegistry(_ => { });
        var registration = registry.Register(
            () => calls.Add("cancel"),
            () => calls.Add("close"),
            () => calls.Add("clear"));

        registration.Dispose();
        registration.Dispose();

        Assert.Empty(calls);
    }

    [Fact]
    public async Task TransientRegistry_CancelAndCloseAll_SkipsAlreadyDisposedRegistrations()
    {
        var calls = new List<string>();
        var registry = new TransientOperationRegistry(_ => { });
        var registration = registry.Register(
            () => calls.Add("cancel"),
            () => calls.Add("close"),
            () => calls.Add("clear"));
        registration.Dispose();

        await registry.CancelAndCloseAllAsync();

        Assert.Empty(calls);
    }

    [Fact]
    public async Task TransientRegistry_CancelAndCloseAllSynchronously_DoesNotHoldInternalLockWhileInvokingCallbacks()
    {
        var registry = new TransientOperationRegistry(_ => { });
        var callbackStarted = new ManualResetEventSlim();
        var releaseCallback = new ManualResetEventSlim();
        registry.Register(
            cancel: () =>
            {
                callbackStarted.Set();
                releaseCallback.Wait(TimeSpan.FromSeconds(5));
            },
            close: () => { },
            clearSensitive: () => { });

        var closeTask = Task.Run(() => registry.CancelAndCloseAllSynchronously());
        Assert.True(callbackStarted.Wait(TimeSpan.FromSeconds(5)), "回调应已开始执行");

        var registerTask = Task.Run(() => registry.Register(() => { }, () => { }, () => { }));
        await registerTask.WaitAsync(TimeSpan.FromSeconds(5));

        releaseCallback.Set();
        await closeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class State(Action clear) : ISensitiveState
    {
        public void ClearSensitive() => clear();
    }
}
