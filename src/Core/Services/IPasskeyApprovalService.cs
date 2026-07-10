namespace Core.Services;

public enum PasskeyUserVerificationRequirement
{
    Preferred,
    Required,
    Discouraged,
}

public readonly record struct PasskeyApprovalEvidence(
    bool Approved,
    bool UserPresent,
    bool UserVerified);

public sealed record PasskeyLoginTarget(string CipherId, string Name);

public sealed record PasskeyApprovalRequest(
    string RequestId,
    string Origin,
    string RpId,
    string CipherId,
    string CredentialId,
    string CipherName,
    string? UserName,
    string? UserDisplayName,
    PasskeyUserVerificationRequirement UserVerificationRequirement);

public sealed record PasskeyCreateApprovalRequest(
    string RequestId,
    string Origin,
    string RpId,
    string? RpName,
    string? UserName,
    string? UserDisplayName,
    IReadOnlyList<PasskeyLoginTarget> ExistingLogins,
    PasskeyUserVerificationRequirement UserVerificationRequirement);

public sealed record PasskeyCreateApprovalResult(
    PasskeyApprovalEvidence Evidence,
    bool CreateNew,
    string? CipherId);

public sealed record PasskeyUseApprovalResult(
    PasskeyApprovalEvidence Evidence,
    string? CipherId,
    string? CredentialId);

public interface IPasskeyApprovalService
{
    Task<PasskeyUseApprovalResult> ConfirmUseAsync(
        PasskeyApprovalRequest request,
        CancellationToken ct = default);

    Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
        PasskeyCreateApprovalRequest request,
        CancellationToken ct = default);
}

