using Core.Models;

namespace Core.Abstractions;

public enum AccountDeletionTombstoneReadStatus
{
    Unavailable,
    Found,
    Absent,
    Corrupt,
}

public enum AccountDeletionTombstoneRemovalReason
{
    ServerRejectedPreparedRequest,
    CancelledBeforeDispatch,
}

public readonly record struct AccountDeletionTombstoneReadResult
{
    private AccountDeletionTombstoneReadResult(
        AccountDeletionTombstoneReadStatus status,
        AccountDeletionTombstoneRecord? record)
    {
        Status = status;
        Record = record;
    }

    public AccountDeletionTombstoneReadStatus Status { get; }
    public AccountDeletionTombstoneRecord? Record { get; }

    public static AccountDeletionTombstoneReadResult Found(
        AccountDeletionTombstoneRecord record) => new(
            AccountDeletionTombstoneReadStatus.Found,
            record ?? throw new ArgumentNullException(nameof(record)));

    public static AccountDeletionTombstoneReadResult Absent { get; } =
        new(AccountDeletionTombstoneReadStatus.Absent, null);

    public static AccountDeletionTombstoneReadResult Corrupt { get; } =
        new(AccountDeletionTombstoneReadStatus.Corrupt, null);

    public static AccountDeletionTombstoneReadResult Unavailable { get; } =
        new(AccountDeletionTombstoneReadStatus.Unavailable, null);
}

public readonly record struct AccountDeletionTombstoneReadAllResult
{
    private AccountDeletionTombstoneReadAllResult(
        AccountDeletionTombstoneReadStatus status,
        IReadOnlyList<AccountDeletionTombstoneRecord> records)
    {
        Status = status;
        Records = records;
    }

    public AccountDeletionTombstoneReadStatus Status { get; }
    public IReadOnlyList<AccountDeletionTombstoneRecord> Records { get; }

    public static AccountDeletionTombstoneReadAllResult Found(
        IReadOnlyList<AccountDeletionTombstoneRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0 || records.Any(record => record is null))
        {
            throw new ArgumentException(
                "A found result must contain at least one non-null record.",
                nameof(records));
        }

        return new(
            AccountDeletionTombstoneReadStatus.Found,
            Array.AsReadOnly(records.ToArray()));
    }

    public static AccountDeletionTombstoneReadAllResult Absent { get; } =
        new(
            AccountDeletionTombstoneReadStatus.Absent,
            Array.Empty<AccountDeletionTombstoneRecord>());

    public static AccountDeletionTombstoneReadAllResult Corrupt { get; } =
        new(
            AccountDeletionTombstoneReadStatus.Corrupt,
            Array.Empty<AccountDeletionTombstoneRecord>());

    public static AccountDeletionTombstoneReadAllResult Unavailable { get; } =
        new(
            AccountDeletionTombstoneReadStatus.Unavailable,
            Array.Empty<AccountDeletionTombstoneRecord>());
}

/// <summary>
/// Durable ledger for account-deletion attempts. Mutations use exact records as
/// compare-and-swap witnesses and never remove entries based on elapsed time.
/// </summary>
public interface IAccountDeletionTombstoneStore
{
    AccountDeletionTombstoneReadResult Read(AccountDeletionBinding binding);

    AccountDeletionTombstoneReadAllResult ReadAll();

    /// <summary>
    /// Creates a prepared record only when the account and complete endpoint scope is absent.
    /// Email and device changes cannot create another record in that scope. Storage failures
    /// and a full ledger fail closed.
    /// </summary>
    AccountDeletionTombstoneRecord? TryCreate(
        AccountDeletionBinding binding,
        DateTimeOffset createdAt);

    /// <summary>
    /// Advances an exact current record either through Prepared, ServerConfirmed, and the
    /// permanent LocalCleanupComplete marker, or directly from Prepared to the permanent
    /// UncertainLocalCleanupComplete marker. Terminal stages cannot be advanced.
    /// </summary>
    AccountDeletionTombstoneRecord? TryAdvance(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionStage nextStage,
        DateTimeOffset updatedAt);

    /// <summary>
    /// Removes an exact current record only after an explicit, reason-specific decision proves
    /// a still-prepared request was not dispatched or must be locally abandoned. Server-confirmed
    /// and locally-complete records are permanent.
    /// </summary>
    bool Remove(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionTombstoneRemovalReason reason);
}
