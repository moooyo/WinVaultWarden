using System.Security.Cryptography;

namespace Core.Models;

public enum AccountDeletionCredentialKind
{
    MasterPassword,
    OneTimePassword,
}

public enum AccountDeletionCredentialRequirement
{
    MasterPassword,
    OneTimePassword,
    FreshOnlineAuthentication,
}

/// <summary>
/// Owns one deletion credential. The deletion service transfers ownership of the
/// mutable character buffer and clears it after deriving the wire credential. Disposing
/// an unused credential clears the buffer when a precondition prevents dispatch.
/// </summary>
public sealed class AccountDeletionCredential : IDisposable
{
    private readonly object _gate = new();
    private char[]? _value;

    private AccountDeletionCredential(
        AccountDeletionCredentialKind kind,
        ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || IsAllWhiteSpace(value))
            throw new ArgumentException("The deletion credential cannot be empty.", nameof(value));

        Kind = kind;
        _value = value.ToArray();
    }

    public AccountDeletionCredentialKind Kind { get; }

    public static AccountDeletionCredential FromMasterPassword(ReadOnlySpan<char> password) =>
        new(AccountDeletionCredentialKind.MasterPassword, password);

    public static AccountDeletionCredential FromOneTimePassword(ReadOnlySpan<char> otp) =>
        new(AccountDeletionCredentialKind.OneTimePassword, otp);

    internal char[] Consume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_value is null, this);
            var value = _value;
            _value = null;
            return value;
        }
    }

    public void Dispose()
    {
        char[]? value;
        lock (_gate)
        {
            value = _value;
            _value = null;
        }

        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }

    private static bool IsAllWhiteSpace(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
                return false;
        }
        return true;
    }
}

public enum AccountDeletionPreflightBlockReason
{
    None,
    ClaimedByOrganization,
    FreshOnlineAuthenticationRequired,
}

public sealed record AccountDeletionOwnerWarning(
    string OrganizationId,
    string OrganizationName,
    int? ProductTierType);

public sealed record AccountDeletionPreflight
{
    public AccountDeletionPreflight(
        AccountDeletionBinding binding,
        AccountDeletionCredentialRequirement credentialRequirement,
        bool isClaimedByOrganization,
        IReadOnlyList<AccountDeletionOwnerWarning> ownerWarnings,
        AccountDeletionPreflightBlockReason blockReason)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(ownerWarnings);
        if (!Enum.IsDefined(credentialRequirement))
            throw new ArgumentOutOfRangeException(nameof(credentialRequirement));
        if (!Enum.IsDefined(blockReason))
            throw new ArgumentOutOfRangeException(nameof(blockReason));
        if (ownerWarnings.Any(warning => warning is null))
            throw new ArgumentException("Owner warnings cannot contain null values.", nameof(ownerWarnings));
        if (isClaimedByOrganization
            && blockReason != AccountDeletionPreflightBlockReason.ClaimedByOrganization)
        {
            throw new ArgumentException(
                "An organization-claimed account must be blocked by the claimed-account rule.",
                nameof(blockReason));
        }
        if (credentialRequirement == AccountDeletionCredentialRequirement.FreshOnlineAuthentication
            && blockReason == AccountDeletionPreflightBlockReason.None)
        {
            throw new ArgumentException(
                "An unknown master-password capability must require fresh online authentication.",
                nameof(blockReason));
        }

        Binding = binding;
        CredentialRequirement = credentialRequirement;
        IsClaimedByOrganization = isClaimedByOrganization;
        OwnerWarnings = Array.AsReadOnly(ownerWarnings.ToArray());
        BlockReason = blockReason;
    }

    public AccountDeletionBinding Binding { get; }
    public AccountDeletionCredentialRequirement CredentialRequirement { get; }
    public bool IsClaimedByOrganization { get; }
    public IReadOnlyList<AccountDeletionOwnerWarning> OwnerWarnings { get; }
    public AccountDeletionPreflightBlockReason BlockReason { get; }
    public bool CanDispatch => BlockReason == AccountDeletionPreflightBlockReason.None;
}

public enum AccountDeletionRejectionReason
{
    RequiresUnlockedSession,
    SessionBindingMismatch,
    FreshOnlineAuthenticationRequired,
    ClaimedByOrganization,
    WrongCredentialKind,
    PendingDeletionExists,
    LocalPersistenceUnavailable,
    LocalPersistenceCorrupt,
    ConcurrentStateChange,
    InvalidCredential,
    ServerRejected,
}

public enum AccountDeletionUncertainReason
{
    ExistingPreparedTombstone,
    CallerCancelledAfterDispatch,
    SessionChangedAfterDispatch,
    RequestTimedOut,
    TransportFailure,
    ServerReturnedUncertainStatus,
    UnexpectedDispatchFailure,
}

public enum AccountDeletionCleanupTarget
{
    CoordinationGate,
    Session,
    Token,
    VaultCache,
    DeviceKey,
    CipherCreatePending,
    TrustedDeviceApproval,
    TdeV2JitPending,
    FaviconCache,
    Tombstone,
}

public enum AccountDeletionCleanupFailureKind
{
    Unavailable,
    Corrupt,
    Changed,
    Failed,
}

public sealed record AccountDeletionCleanupFailure(
    AccountDeletionCleanupTarget Target,
    AccountDeletionCleanupFailureKind Kind,
    string? Detail = null);

public abstract record AccountDeletionResult
{
    private AccountDeletionResult()
    {
    }

    public sealed record Confirmed(AccountDeletionPreflight? Preflight)
        : AccountDeletionResult;

    public sealed record Rejected(
        AccountDeletionRejectionReason Reason,
        AccountDeletionPreflight? Preflight = null,
        int? HttpStatusCode = null,
        string? Message = null,
        AccountDeletionTombstoneRecord? Pending = null)
        : AccountDeletionResult;

    public sealed record CancelledBeforeDispatch(
        AccountDeletionPreflight Preflight,
        AccountDeletionTombstoneRecord? Pending)
        : AccountDeletionResult;

    public sealed record OutcomeUncertain(
        AccountDeletionUncertainReason Reason,
        AccountDeletionTombstoneRecord Pending,
        AccountDeletionPreflight? Preflight = null,
        int? HttpStatusCode = null,
        string? Message = null)
        : AccountDeletionResult;

    /// <summary>
    /// The server outcome remains unknown, but local account-scoped cleanup completed and
    /// the permanent uncertainty marker was durably recorded.
    /// </summary>
    public sealed record OutcomeUncertainLocalCleanupComplete(
        AccountDeletionTombstoneRecord Pending,
        AccountDeletionPreflight? Preflight = null)
        : AccountDeletionResult;

    /// <summary>
    /// The server outcome remains unknown and at least one local cleanup target could not
    /// be proven absent. The durable prepared or permanent uncertainty record remains the
    /// recovery authority.
    /// </summary>
    public sealed record OutcomeUncertainLocalCleanupIncomplete
        : AccountDeletionResult
    {
        public OutcomeUncertainLocalCleanupIncomplete(
            AccountDeletionTombstoneRecord pending,
            IReadOnlyList<AccountDeletionCleanupFailure> failures,
            AccountDeletionPreflight? preflight = null)
        {
            Pending = pending ?? throw new ArgumentNullException(nameof(pending));
            ArgumentNullException.ThrowIfNull(failures);
            if (failures.Count == 0 || failures.Any(failure => failure is null))
            {
                throw new ArgumentException(
                    "Incomplete cleanup requires at least one non-null failure.",
                    nameof(failures));
            }
            Failures = Array.AsReadOnly(failures.ToArray());
            Preflight = preflight;
        }

        public AccountDeletionTombstoneRecord Pending { get; }
        public IReadOnlyList<AccountDeletionCleanupFailure> Failures { get; }
        public AccountDeletionPreflight? Preflight { get; }
    }

    public sealed record ConfirmedLocalCleanupIncomplete
        : AccountDeletionResult
    {
        public ConfirmedLocalCleanupIncomplete(
            AccountDeletionTombstoneRecord pending,
            IReadOnlyList<AccountDeletionCleanupFailure> failures,
            AccountDeletionPreflight? preflight = null)
        {
            Pending = pending ?? throw new ArgumentNullException(nameof(pending));
            ArgumentNullException.ThrowIfNull(failures);
            if (failures.Count == 0 || failures.Any(failure => failure is null))
            {
                throw new ArgumentException(
                    "Incomplete cleanup requires at least one non-null failure.",
                    nameof(failures));
            }
            Failures = Array.AsReadOnly(failures.ToArray());
            Preflight = preflight;
        }

        public AccountDeletionTombstoneRecord Pending { get; }
        public IReadOnlyList<AccountDeletionCleanupFailure> Failures { get; }
        public AccountDeletionPreflight? Preflight { get; }
    }
}

public abstract record AccountDeletionOtpRequestResult
{
    private AccountDeletionOtpRequestResult()
    {
    }

    public sealed record Sent(AccountDeletionPreflight Preflight)
        : AccountDeletionOtpRequestResult;

    public sealed record Rejected(
        AccountDeletionRejectionReason Reason,
        AccountDeletionPreflight? Preflight = null,
        int? HttpStatusCode = null,
        string? Message = null)
        : AccountDeletionOtpRequestResult;

    public sealed record CancelledBeforeDispatch(AccountDeletionPreflight Preflight)
        : AccountDeletionOtpRequestResult;

    public sealed record OutcomeUncertain(
        AccountDeletionPreflight Preflight,
        int? HttpStatusCode = null,
        string? Message = null)
        : AccountDeletionOtpRequestResult;
}

public enum AccountDeletionLocalResolutionStatus
{
    Completed,
    NotFound,
    WrongStage,
    Changed,
    Corrupt,
    Unavailable,
    CancelledBeforeCleanup,
    CleanupIncomplete,
}

public sealed record AccountDeletionLocalResolutionResult
{
    public AccountDeletionLocalResolutionResult(
        AccountDeletionLocalResolutionStatus status,
        AccountDeletionTombstoneRecord? pending = null,
        IReadOnlyList<AccountDeletionCleanupFailure>? failures = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        Pending = pending;
        Failures = Array.AsReadOnly(
            (failures ?? Array.Empty<AccountDeletionCleanupFailure>()).ToArray());
    }

    public AccountDeletionLocalResolutionStatus Status { get; }
    public AccountDeletionTombstoneRecord? Pending { get; }
    public IReadOnlyList<AccountDeletionCleanupFailure> Failures { get; }
}
