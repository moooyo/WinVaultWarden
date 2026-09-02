using Core.Models;

namespace Core.Abstractions;

public enum LoginWithDeviceQuarantineStoreReadStatus
{
    Unavailable,
    Found,
    Absent,
    Corrupt,
}

public enum LoginWithDeviceQuarantineRemovalReason
{
    DefiniteOutcome,
    SessionPersisted,
    UserConfirmedAfterEligibility,
}

public readonly record struct LoginWithDeviceQuarantineStoreReadResult
{
    private LoginWithDeviceQuarantineStoreReadResult(
        LoginWithDeviceQuarantineStoreReadStatus status,
        LoginWithDeviceQuarantineRecord? record)
    {
        Status = status;
        Record = record;
    }

    public LoginWithDeviceQuarantineStoreReadStatus Status { get; }

    public LoginWithDeviceQuarantineRecord? Record { get; }

    public static LoginWithDeviceQuarantineStoreReadResult Found(
        LoginWithDeviceQuarantineRecord record) =>
        new(
            LoginWithDeviceQuarantineStoreReadStatus.Found,
            record ?? throw new ArgumentNullException(nameof(record)));

    public static LoginWithDeviceQuarantineStoreReadResult Absent { get; } =
        new(LoginWithDeviceQuarantineStoreReadStatus.Absent, null);

    public static LoginWithDeviceQuarantineStoreReadResult Corrupt { get; } =
        new(LoginWithDeviceQuarantineStoreReadStatus.Corrupt, null);

    public static LoginWithDeviceQuarantineStoreReadResult Unavailable { get; } =
        new(LoginWithDeviceQuarantineStoreReadStatus.Unavailable, null);
}

/// <summary>
/// Durable fail-closed ledger for device-login requests whose token-exchange outcome
/// may be unknown. Entries are never removed automatically based on elapsed time.
/// </summary>
public interface ILoginWithDeviceQuarantineStore
{
    /// <summary>
    /// Reads the only quarantine entry for a complete server, account, and device binding.
    /// </summary>
    LoginWithDeviceQuarantineStoreReadResult Read(
        LoginWithDeviceQuarantineBinding binding);

    /// <summary>
    /// Atomically arms a quarantine entry only when its binding is absent. Returns null when
    /// the binding is already quarantined. Storage failures and a full ledger fail closed.
    /// </summary>
    LoginWithDeviceQuarantineRecord? TryArm(
        LoginWithDeviceQuarantineBinding binding,
        string requestId,
        DateTimeOffset armedAt,
        DateTimeOffset requestExpiresAt,
        DateTimeOffset clearEligibleAt);

    /// <summary>
    /// Removes an exact current record by compare-and-swap. User-confirmed removal is rejected
    /// until the record's clear-eligibility timestamp has passed.
    /// </summary>
    bool Remove(
        LoginWithDeviceQuarantineRecord expected,
        LoginWithDeviceQuarantineRemovalReason reason);
}
