using App.Services;
using Xunit;

namespace App.Tests;

public sealed class CancelWinsTaskTests
{
    [Fact]
    public async Task InitiallyCancelledCallerStillReleasesTheOperationAlreadyInFlight()
    {
        var operation = new TaskCompletionSource<Probe>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelWinsTask.AwaitAsync(
            operation.Task, static value => value.DisposeAsync(), cancellation.Token));

        var late = new Probe();
        operation.SetResult(late);
        await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, late.DisposeCalls);
    }

    [Fact]
    public async Task InitiallyCancelledCallerReleasesAnAlreadyCompletedResultExactlyOnce()
    {
        var probe = new Probe();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelWinsTask.AwaitAsync(
            Task.FromResult(probe), static value => value.DisposeAsync(), cancellation.Token));

        await probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, probe.DisposeCalls);
    }

    [Fact]
    public async Task InitiallyCancelledCallerRemainsCancelledWhenTheOperationHasAlreadyFaulted()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CancelWinsTask.AwaitAsync(
            Task.FromException<Probe>(new IOException("Synthetic completed operation failure.")),
            static value => value.DisposeAsync(), cancellation.Token));
    }

    [Fact]
    public async Task CancellationReturnsPromptlyAndDisposesLateSuccessfulResult()
    {
        var operation = new TaskCompletionSource<Probe>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var waiting = CancelWinsTask.AwaitAsync(
            operation.Task,
            static probe => probe.DisposeAsync(),
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var late = new Probe();
        operation.SetResult(late);
        await late.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SuccessfulResultRemainsOwnedByCaller()
    {
        var probe = new Probe();

        var result = await CancelWinsTask.AwaitAsync(
            Task.FromResult(probe),
            static value => value.DisposeAsync(),
            CancellationToken.None);

        Assert.Same(probe, result);
        Assert.False(probe.Disposed.Task.IsCompleted);
        await result.DisposeAsync();
    }

    [Fact]
    public async Task LateFaultIsObservedWithoutReplacingCancellation()
    {
        var operation = new TaskCompletionSource<Probe>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var waiting = CancelWinsTask.AwaitAsync(
            operation.Task,
            static probe => probe.DisposeAsync(),
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        operation.SetException(new IOException("late failure"));
        await Task.Yield();
    }

    private sealed class Probe : IAsyncDisposable
    {
        private int _disposeCalls;
        public int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
