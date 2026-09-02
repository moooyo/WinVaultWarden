using Core.Models;

namespace Core.Abstractions;

public enum TdeV2JitPendingReadStatus
{
    Unavailable,
    Found,
    Absent,
    Corrupt,
}

public enum TdeV2JitPendingRemovalReason
{
    Completed,
    ExplicitlyAbandoned,
}

public sealed class TdeV2JitPendingStoreRecord : IDisposable
{
    public TdeV2JitPendingStoreRecord(
        Guid entryId,
        long revision,
        TdeV2JitPendingStage stage,
        TdeV2JitPending pending)
    {
        if (entryId == Guid.Empty)
            throw new ArgumentException("Entry identifier cannot be empty.", nameof(entryId));
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage));
        EntryId = entryId;
        Revision = revision;
        Stage = stage;
        Pending = pending ?? throw new ArgumentNullException(nameof(pending));
    }

    public Guid EntryId { get; }
    public long Revision { get; }
    public TdeV2JitPendingStage Stage { get; }
    public TdeV2JitPending Pending { get; }

    public void Dispose() => Pending.Dispose();
}

public readonly record struct TdeV2JitPendingReadResult : IDisposable
{
    private TdeV2JitPendingReadResult(
        TdeV2JitPendingReadStatus status,
        TdeV2JitPendingStoreRecord? record)
    {
        Status = status;
        Record = record;
    }

    public TdeV2JitPendingReadStatus Status { get; }
    public TdeV2JitPendingStoreRecord? Record { get; }

    public void Dispose() => Record?.Dispose();

    public static TdeV2JitPendingReadResult Found(TdeV2JitPendingStoreRecord record) =>
        new(
            TdeV2JitPendingReadStatus.Found,
            record ?? throw new ArgumentNullException(nameof(record)));

    public static TdeV2JitPendingReadResult Absent { get; } =
        new(TdeV2JitPendingReadStatus.Absent, null);

    public static TdeV2JitPendingReadResult Corrupt { get; } =
        new(TdeV2JitPendingReadStatus.Corrupt, null);

    public static TdeV2JitPendingReadResult Unavailable { get; } =
        new(TdeV2JitPendingReadStatus.Unavailable, null);
}

/// <summary>
/// Durable recovery state for V2 TDE JIT provisioning. A reservation must exist before the first
/// POST /accounts/keys attempt, and every subsequent attempt must reuse its exact request.
/// </summary>
public interface ITdeV2JitPendingStore
{
    /// <summary>
    /// Reads the only pending enrollment for a complete account/server/device binding. A Found
    /// result owns sensitive key material and must be disposed.
    /// </summary>
    TdeV2JitPendingReadResult Read(DeviceKeyBinding binding);

    /// <summary>
    /// Atomically creates a Prepared reservation. Returns null when the binding is already reserved.
    /// The returned record owns an independent sensitive-state copy and must be disposed.
    /// </summary>
    TdeV2JitPendingStoreRecord? TryCreate(TdeV2JitPending pending);

    /// <summary>
    /// Atomically applies a strictly monotonic stage update to an exact current record. On success,
    /// the supplied record is disposed and the returned record owns a new sensitive-state copy. On
    /// a stale-CAS failure, null is returned and the caller retains ownership of the supplied record.
    /// </summary>
    TdeV2JitPendingStoreRecord? TryAdvance(
        TdeV2JitPendingStoreRecord expected,
        TdeV2JitPendingStage nextStage);

    /// <summary>
    /// Removes an exact current record. Completed removal is accepted only from LocalCommitReady;
    /// ExplicitlyAbandoned is an irreversible local recovery action. Success disposes the record.
    /// </summary>
    bool Remove(
        TdeV2JitPendingStoreRecord expected,
        TdeV2JitPendingRemovalReason reason);

    /// <summary>
    /// Removes every pending enrollment for one account and complete server environment.
    /// The default fails closed for stores without canonical account-scope enumeration.
    /// </summary>
    AccountScopedRemovalStatus RemoveAccountScope(
        string accountId,
        ServerEndpoints endpoints)
    {
        AccountScopedRemovalValidation.Validate(accountId, endpoints);
        return AccountScopedRemovalStatus.Unavailable;
    }
}
