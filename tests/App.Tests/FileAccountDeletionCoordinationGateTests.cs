using App.Services;
using Core.Abstractions;
using Xunit;

namespace App.Tests;

public sealed class FileAccountDeletionCoordinationGateTests
{
    [Fact]
    public void TryAcquire_SynchronousOuterAllowsNestedSynchronousLease()
    {
        var path = NewPath();
        IAccountDeletionCoordinationLease? outer = null;
        IAccountDeletionCoordinationLease? nested = null;
        IAccountDeletionCoordinationLease? afterRelease = null;
        try
        {
            var gate = new FileAccountDeletionCoordinationGate(path);
            var contender = new FileAccountDeletionCoordinationGate(path);
            outer = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                gate.TryAcquire());
            nested = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                gate.TryAcquire());
            Assert.Null(contender.TryAcquire());

            outer.Dispose();
            outer = null;
            Assert.Null(contender.TryAcquire());
            nested.Dispose();
            nested = null;

            afterRelease = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                contender.TryAcquire());
        }
        finally
        {
            afterRelease?.Dispose();
            nested?.Dispose();
            outer?.Dispose();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task AcquireAsync_OuterAllowsNestedSynchronousLease()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var path = NewPath();
        IAccountDeletionCoordinationLease? outer = null;
        IAccountDeletionCoordinationLease? nested = null;
        try
        {
            var gate = new FileAccountDeletionCoordinationGate(path);
            outer = await gate.AcquireAsync(testTimeout.Token);
            nested = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                gate.TryAcquire());

            nested.Dispose();
            nested = null;
            await outer.DisposeAsync();
            outer = null;
            using var reacquired = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                gate.TryAcquire());
        }
        finally
        {
            nested?.Dispose();
            if (outer is not null)
                await outer.DisposeAsync();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task TryAcquire_ProcessAndFileContentionReturnNullWithoutWaiting()
    {
        var path = NewPath();
        IAccountDeletionCoordinationLease? outer = null;
        try
        {
            var gate = new FileAccountDeletionCoordinationGate(path);
            var otherInstance = new FileAccountDeletionCoordinationGate(path);
            outer = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                gate.TryAcquire());
            Assert.Null(otherInstance.TryAcquire());

            Task<IAccountDeletionCoordinationLease?> sameInstanceAttempt;
            using (ExecutionContext.SuppressFlow())
                sameInstanceAttempt = Task.Run(gate.TryAcquire);
            Assert.Null(await sameInstanceAttempt.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            outer?.Dispose();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task DisposeAndDisposeAsync_AreSharedIdempotentReleasePaths()
    {
        var path = NewPath();
        try
        {
            var gate = new FileAccountDeletionCoordinationGate(path);
            var first = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                gate.TryAcquire());
            first.Dispose();
            first.Dispose();
            await first.DisposeAsync();

            var second = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                gate.TryAcquire());
            await second.DisposeAsync();
            second.Dispose();

            using var third = Assert.IsAssignableFrom<IAccountDeletionCoordinationLease>(
                new FileAccountDeletionCoordinationGate(path).TryAcquire());
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceAcquire_IsMutuallyExclusiveAndReleaseHandsOff()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = testTimeout.Token;
        var path = NewPath();
        var firstGate = new FileAccountDeletionCoordinationGate(path);
        var secondGate = new FileAccountDeletionCoordinationGate(path);
        IAccountDeletionCoordinationLease? firstLease = null;
        try
        {
            firstLease = await firstGate.AcquireAsync(ct);
            var started = NewSignal();
            var acquired = NewSignal();
            var contender = Task.Run(async () =>
            {
                started.TrySetResult();
                await using var lease = await secondGate.AcquireAsync(
                    ct);
                acquired.TrySetResult();
            }, ct);

            await started.Task.WaitAsync(ct);
            await AssertRemainsBlockedAsync(acquired.Task, ct);
            await firstLease.DisposeAsync();
            firstLease = null;
            await acquired.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                ct);
            await contender.WaitAsync(ct);
        }
        finally
        {
            if (firstLease is not null)
                await firstLease.DisposeAsync();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task SameInstanceIndependentFlow_IsSerializedByProcessGate()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = testTimeout.Token;
        var path = NewPath();
        var gate = new FileAccountDeletionCoordinationGate(path);
        IAccountDeletionCoordinationLease? firstLease = null;
        try
        {
            firstLease = await gate.AcquireAsync(ct);
            var acquired = NewSignal();
            Task contender;
            using (ExecutionContext.SuppressFlow())
            {
                contender = Task.Run(async () =>
                {
                    await using var lease = await gate.AcquireAsync(
                        ct);
                    acquired.TrySetResult();
                }, ct);
            }

            await AssertRemainsBlockedAsync(acquired.Task, ct);
            await firstLease.DisposeAsync();
            firstLease = null;
            await acquired.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                ct);
            await contender.WaitAsync(ct);
        }
        finally
        {
            if (firstLease is not null)
                await firstLease.DisposeAsync();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task SameAsyncFlow_ReentrantLeaseKeepsUnderlyingLockUntilLastDispose()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = testTimeout.Token;
        var path = NewPath();
        var gate = new FileAccountDeletionCoordinationGate(path);
        var contenderGate = new FileAccountDeletionCoordinationGate(path);
        IAccountDeletionCoordinationLease? outer = null;
        IAccountDeletionCoordinationLease? nested = null;
        try
        {
            outer = await gate.AcquireAsync(ct);
            nested = await gate.AcquireAsync(ct);
            var acquired = NewSignal();
            var contender = Task.Run(async () =>
            {
                await using var lease = await contenderGate.AcquireAsync(
                    ct);
                acquired.TrySetResult();
            }, ct);

            await AssertRemainsBlockedAsync(acquired.Task, ct);
            await outer.DisposeAsync();
            outer = null;
            await AssertRemainsBlockedAsync(acquired.Task, ct);
            await nested.DisposeAsync();
            nested = null;
            await acquired.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                ct);
            await contender.WaitAsync(ct);
        }
        finally
        {
            if (nested is not null)
                await nested.DisposeAsync();
            if (outer is not null)
                await outer.DisposeAsync();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task WaitingAcquire_CanBeCancelledWithoutPoisoningGate()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = testTimeout.Token;
        var path = NewPath();
        var firstGate = new FileAccountDeletionCoordinationGate(path);
        var waitingGate = new FileAccountDeletionCoordinationGate(path);
        IAccountDeletionCoordinationLease? firstLease = null;
        try
        {
            firstLease = await firstGate.AcquireAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await waitingGate.AcquireAsync(timeout.Token));

            await firstLease.DisposeAsync();
            firstLease = null;
            await using var recovered = await waitingGate.AcquireAsync(
                ct);
        }
        finally
        {
            if (firstLease is not null)
                await firstLease.DisposeAsync();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task FileLock_WorksOnCurrentPlatformAndLockFileIsRetained()
    {
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = testTimeout.Token;
        var path = NewPath();
        try
        {
            var gate = new FileAccountDeletionCoordinationGate(path);
            await using (await gate.AcquireAsync(ct))
            {
                Assert.True(File.Exists(path));
            }

            Assert.True(File.Exists(path));
            await using var reacquired = await new FileAccountDeletionCoordinationGate(path)
                .AcquireAsync(ct);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task AssertRemainsBlockedAsync(
        Task task,
        CancellationToken cancellationToken)
    {
        var delay = Task.Delay(
            TimeSpan.FromMilliseconds(100),
            cancellationToken);
        Assert.NotSame(task, await Task.WhenAny(task, delay));
        await delay;
    }

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "account-deletion.lock");

    private static void DeleteScope(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
