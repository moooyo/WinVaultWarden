using Core.Models;

namespace Core.Services;

public sealed class CipherCreateSnapshotMismatchException : Exception
{
    public CipherCreateSnapshotMismatchException(
        string message,
        CipherCreatePending pending,
        CipherCreateReconciliationResult reconciliation)
        : base(message)
    {
        Pending = pending ?? throw new ArgumentNullException(nameof(pending));
        Reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
    }

    public CipherCreatePending Pending { get; }
    public CipherCreateReconciliationResult Reconciliation { get; }
}
