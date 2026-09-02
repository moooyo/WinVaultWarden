using Core.Session;

namespace Core.Services;

public enum TrustedDeviceApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
}

public enum TrustedDeviceApprovalMethod
{
    Device,
    Administrator,
}

public sealed record TrustedDeviceApprovalRequest(
    string Id,
    string FingerprintPhrase,
    DateTimeOffset ExpiresAt);

public sealed record TrustedDeviceApprovalPollResult(
    TrustedDeviceApprovalStatus Status,
    // The caller owns this buffer and must transfer or zero it.
    byte[]? UserKey = null);

public interface ITrustedDeviceApprovalService
{
    Task<TrustedDeviceApprovalRequest> BeginAsync(
        VaultSessionLease lease,
        string email,
        string deviceIdentifier,
        CancellationToken ct = default);

    Task<TrustedDeviceApprovalRequest> BeginAsync(
        VaultSessionLease lease,
        string email,
        string deviceIdentifier,
        TrustedDeviceApprovalMethod method,
        CancellationToken ct = default) =>
        method == TrustedDeviceApprovalMethod.Device
            ? BeginAsync(lease, email, deviceIdentifier, ct)
            : Task.FromException<TrustedDeviceApprovalRequest>(
                new TrustedDeviceApprovalException(
                    "Administrator device approval is unavailable."));

    Task<TrustedDeviceApprovalPollResult> PollAsync(
        VaultSessionLease lease,
        string requestId,
        CancellationToken ct = default);

    void Cancel(VaultSessionLease lease);

    void Abandon(VaultSessionLease lease) => Cancel(lease);
}

public sealed class TrustedDeviceApprovalException : Exception
{
    public TrustedDeviceApprovalException(string message)
        : base(message)
    {
    }

    public TrustedDeviceApprovalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
