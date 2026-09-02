using Core.Models;

namespace Core.Abstractions;

public enum CipherCreatePendingReadStatus
{
    Unavailable,
    Found,
    Absent,
    Corrupt,
}

public sealed record CipherCreatePendingRecord
{
    public CipherCreatePendingRecord(
        Guid reservationId,
        long revision,
        bool isInFlight,
        CipherCreatePending pending)
    {
        if (reservationId == Guid.Empty)
            throw new ArgumentException("Reservation identifier cannot be empty.", nameof(reservationId));
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        ReservationId = reservationId;
        Revision = revision;
        IsInFlight = isInFlight;
        Pending = pending ?? throw new ArgumentNullException(nameof(pending));
    }

    public Guid ReservationId { get; }
    public long Revision { get; }
    public bool IsInFlight { get; }
    public CipherCreatePending Pending { get; }
}

public readonly record struct CipherCreatePendingReadResult
{
    private CipherCreatePendingReadResult(
        CipherCreatePendingReadStatus status,
        CipherCreatePendingRecord? record)
    {
        Status = status;
        Record = record;
    }

    public CipherCreatePendingReadStatus Status { get; }
    public CipherCreatePendingRecord? Record { get; }
    public CipherCreatePending? Pending => Record?.Pending;

    public static CipherCreatePendingReadResult Found(CipherCreatePendingRecord record) =>
        new(
            CipherCreatePendingReadStatus.Found,
            record ?? throw new ArgumentNullException(nameof(record)));

    public static CipherCreatePendingReadResult Absent { get; } =
        new(CipherCreatePendingReadStatus.Absent, null);

    public static CipherCreatePendingReadResult Corrupt { get; } =
        new(CipherCreatePendingReadStatus.Corrupt, null);

    public static CipherCreatePendingReadResult Unavailable { get; } =
        new(CipherCreatePendingReadStatus.Unavailable, null);
}

public interface ICipherCreatePendingReservation : IDisposable
{
    CipherCreatePendingRecord Record { get; }
}

/// <summary>
/// Durable ledger for create-cipher operations whose server outcome must be reconciled before a
/// second create can be dispatched for the same account and complete endpoint set.
/// </summary>
public interface ICipherCreatePendingStore
{
    CipherCreatePendingReadResult Read(string accountId, ServerEndpoints endpoints);

    /// <summary>
    /// Atomically creates a reservation. Returns null when the scope is already reserved.
    /// </summary>
    ICipherCreatePendingReservation? TryCreate(CipherCreatePending pending);

    /// <summary>
    /// Atomically applies a monotonic update to a matching reservation. Returns null when the
    /// reservation is absent or belongs to another witness.
    /// </summary>
    CipherCreatePendingRecord? TryUpdate(
        CipherCreatePendingRecord expected,
        CipherCreatePending pending);

    /// <summary>
    /// Atomically releases an owned in-flight reservation while applying its final monotonic state.
    /// </summary>
    CipherCreatePendingRecord? Release(
        CipherCreatePendingRecord expected,
        CipherCreatePending pending);

    /// <summary>
    /// Removes a matching reservation after authoritative reconciliation confirmed the create.
    /// </summary>
    bool RemoveConfirmed(CipherCreatePendingRecord expected);

    /// <summary>
    /// Explicitly abandons a matching reservation without network activity.
    /// </summary>
    bool Abandon(CipherCreatePendingRecord expected);
}
