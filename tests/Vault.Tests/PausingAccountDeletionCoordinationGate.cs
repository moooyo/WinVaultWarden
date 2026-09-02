using Core.Abstractions;

namespace Vault.Tests;

internal sealed class PausingAccountDeletionCoordinationGate
    : IAccountDeletionCoordinationGate
{
    private readonly TaskCompletionSource _acquireStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task AcquireStarted => _acquireStarted.Task;
    public int DisposeCount { get; private set; }

    public IAccountDeletionCoordinationLease? TryAcquire() => null;

    public async ValueTask<IAccountDeletionCoordinationLease> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        _acquireStarted.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    public void Release() => _release.TrySetResult();

    private sealed class Lease(PausingAccountDeletionCoordinationGate owner)
        : IAccountDeletionCoordinationLease
    {
        private PausingAccountDeletionCoordinationGate? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
                owner.DisposeCount++;
        }
    }
}

internal sealed class AmbientTrackingAccountDeletionCoordinationGate
    : IAccountDeletionCoordinationGate
{
    private readonly AsyncLocal<int> _depth = new();

    public int OuterAcquisitionCount { get; private set; }
    public int ReentrantAcquisitionCount { get; private set; }

    public IAccountDeletionCoordinationLease? TryAcquire() => AcquireCore();

    public ValueTask<IAccountDeletionCoordinationLease> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAccountDeletionCoordinationLease>(AcquireCore());
    }

    private IAccountDeletionCoordinationLease AcquireCore()
    {
        if (_depth.Value > 0)
        {
            _depth.Value++;
            ReentrantAcquisitionCount++;
        }
        else
        {
            _depth.Value = 1;
            OuterAcquisitionCount++;
        }
        return new Lease(this);
    }

    private sealed class Lease(AmbientTrackingAccountDeletionCoordinationGate owner)
        : IAccountDeletionCoordinationLease
    {
        private AmbientTrackingAccountDeletionCoordinationGate? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
                owner._depth.Value--;
        }
    }
}
