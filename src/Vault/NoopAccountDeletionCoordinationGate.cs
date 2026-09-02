using Core.Abstractions;

namespace Vault;

internal sealed class NoopAccountDeletionCoordinationGate
    : IAccountDeletionCoordinationGate,
      IAccountDeletionCoordinationLease
{
    public static NoopAccountDeletionCoordinationGate Instance { get; } = new();

    private NoopAccountDeletionCoordinationGate()
    {
    }

    public IAccountDeletionCoordinationLease? TryAcquire() => this;

    public ValueTask<IAccountDeletionCoordinationLease> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAccountDeletionCoordinationLease>(this);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
    }
}
