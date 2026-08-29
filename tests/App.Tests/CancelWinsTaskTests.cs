using App.Services;
using Xunit;

namespace App.Tests;

public sealed class CancelWinsTaskTests
{
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
        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
