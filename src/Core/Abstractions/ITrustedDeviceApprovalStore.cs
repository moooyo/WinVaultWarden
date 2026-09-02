using Core.Models;

namespace Core.Abstractions;

public enum TrustedDeviceApprovalStoreReadStatus
{
    Unavailable,
    Found,
    Absent,
    Corrupt,
}

public enum TrustedDeviceApprovalRemovalReason
{
    Completed,
    // Irreversible user abandonment, not a transient stop-polling or network cancellation.
    Cancelled,
    Expired,
}

public sealed class TrustedDeviceApprovalStoreRecord : IDisposable
{
    public TrustedDeviceApprovalStoreRecord(
        Guid entryId,
        long revision,
        TrustedDeviceApprovalPending pending)
    {
        if (entryId == Guid.Empty)
            throw new ArgumentException("Entry identifier cannot be empty.", nameof(entryId));
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        EntryId = entryId;
        Revision = revision;
        Pending = pending ?? throw new ArgumentNullException(nameof(pending));
    }

    public Guid EntryId { get; }
    public long Revision { get; }
    public TrustedDeviceApprovalPending Pending { get; }

    public void Dispose() => Pending.Dispose();
}

public readonly record struct TrustedDeviceApprovalStoreReadResult : IDisposable
{
    private TrustedDeviceApprovalStoreReadResult(
        TrustedDeviceApprovalStoreReadStatus status,
        TrustedDeviceApprovalStoreRecord? record)
    {
        Status = status;
        Record = record;
    }

    public TrustedDeviceApprovalStoreReadStatus Status { get; }
    public TrustedDeviceApprovalStoreRecord? Record { get; }

    public void Dispose() => Record?.Dispose();

    public static TrustedDeviceApprovalStoreReadResult Found(
        TrustedDeviceApprovalStoreRecord record) =>
        new(
            TrustedDeviceApprovalStoreReadStatus.Found,
            record ?? throw new ArgumentNullException(nameof(record)));

    public static TrustedDeviceApprovalStoreReadResult Absent { get; } =
        new(TrustedDeviceApprovalStoreReadStatus.Absent, null);

    public static TrustedDeviceApprovalStoreReadResult Corrupt { get; } =
        new(TrustedDeviceApprovalStoreReadStatus.Corrupt, null);

    public static TrustedDeviceApprovalStoreReadResult Unavailable { get; } =
        new(TrustedDeviceApprovalStoreReadStatus.Unavailable, null);
}

/// <summary>
/// Holds the cross-process right to begin one administrator approval for a complete binding.
/// </summary>
public interface ITrustedDeviceApprovalIntentLease : IDisposable
{
    DeviceKeyBinding Binding { get; }
}

/// <summary>
/// Durable storage for the client private key of a pending trusted-device approval request.
/// </summary>
public interface ITrustedDeviceApprovalStore
{
    /// <summary>
    /// Reads the pending request for a binding. A Found result owns a private-key record that the
    /// caller must dispose, including when the record will not be used.
    /// </summary>
    TrustedDeviceApprovalStoreReadResult Read(DeviceKeyBinding binding);

    /// <summary>
    /// Reads one exact binding/request pair. A Found result owns a private-key record that the
    /// caller must dispose, including when the record will not be used.
    /// </summary>
    TrustedDeviceApprovalStoreReadResult Read(
        DeviceKeyBinding binding,
        string requestId);

    /// <summary>
    /// Acquires the cross-process administrator-request intent for a binding and rechecks that no
    /// durable request exists. Returns null when another process owns the intent or a record exists.
    /// The caller must acquire this before generating keys, hold it across the remote create call,
    /// and dispose it only after TryCreate has durably committed the returned request.
    /// </summary>
    ITrustedDeviceApprovalIntentLease? TryAcquireBeginIntent(DeviceKeyBinding binding);

    /// <summary>
    /// Creates the only pending approval allowed for a complete account/server/device binding while
    /// the matching begin intent remains held. The returned record owns an independent private-key
    /// copy and must be disposed.
    /// </summary>
    TrustedDeviceApprovalStoreRecord? TryCreate(
        ITrustedDeviceApprovalIntentLease intent,
        TrustedDeviceApprovalPending pending);

    /// <summary>
    /// Removes an exact current record. On success this method also disposes the supplied record.
    /// Callers must not map transient polling cancellation or network failure to removal.
    /// </summary>
    bool Remove(
        TrustedDeviceApprovalStoreRecord expected,
        TrustedDeviceApprovalRemovalReason reason);

    /// <summary>
    /// Removes every pending approval for one account and complete server environment.
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
