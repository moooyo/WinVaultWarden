using Core.Session;

namespace Core.Services;

public sealed record TdeV2JitProvisioningResult(
    VaultSessionLease Lease,
    string UserKeyId,
    bool CurrentDeviceTrusted);

public enum TdeV2JitProvisioningStage
{
    CapabilityCheck,
    Preparation,
    AccountKeys,
    ResetPasswordEnrollment,
    DeviceTrust,
    LocalCommit,
    Bootstrap,
}

public interface ITdeV2JitProvisioningService
{
    Task<TdeV2JitProvisioningResult> ProvisionAsync(
        VaultSessionLease authenticatedSession,
        string organizationIdentifier,
        bool trustCurrentDevice,
        CancellationToken ct = default);

    Task<TdeV2JitProvisioningResult> ProvisionAsync(
        VaultSessionLease authenticatedSession,
        string organizationId,
        string userId,
        ReadOnlyMemory<byte> organizationPublicKeySpki,
        bool trustCurrentDevice,
        CancellationToken ct = default);
}

public sealed class TdeV2JitProvisioningException : Exception
{
    public TdeV2JitProvisioningException(
        TdeV2JitProvisioningStage stage,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
    }

    public TdeV2JitProvisioningStage Stage { get; }

    public bool AccountKeysMayHaveBeenCommitted =>
        Stage >= TdeV2JitProvisioningStage.AccountKeys;
}
