namespace Core.Models;

public enum RecoveryCodeAvailability
{
    Available,
    TemporarilyUnavailable,
}

public sealed record TotpActivationResult
{
    private TotpActivationResult(
        RecoveryCodeAvailability recoveryCodeAvailability,
        string? recoveryCode)
    {
        RecoveryCodeAvailability = recoveryCodeAvailability;
        RecoveryCode = recoveryCode;
    }

    public bool IsEnabled => true;

    public RecoveryCodeAvailability RecoveryCodeAvailability { get; }

    public string? RecoveryCode { get; }

    public bool HasRecoveryCode =>
        RecoveryCodeAvailability == Core.Models.RecoveryCodeAvailability.Available
        && !string.IsNullOrWhiteSpace(RecoveryCode);

    public static TotpActivationResult EnabledWithRecoveryCode(string recoveryCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryCode);
        return new(RecoveryCodeAvailability.Available, recoveryCode);
    }

    public static TotpActivationResult EnabledWithoutRecoveryCode() =>
        new(RecoveryCodeAvailability.TemporarilyUnavailable, null);
}
