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

// A single credential the user may pick for an assertion. Carries only display metadata plus
// the ids the handler validates the returned selection against — never a private key or secret.
public sealed record PasskeyCredentialTarget(
    string CipherId,
    string CredentialId,
    string CipherName,
    string? UserName,
    string? UserDisplayName,
    bool RequiresReprompt)
{
    public string DisplayName =>
        string.Join(
            " · ",
            new[] { UserDisplayName, UserName, CipherName }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.CurrentCultureIgnoreCase));
}

// An existing login a newly-created passkey may be attached to. RequiresReprompt lets the
// approval UI gate the attach target the same way an assertion gates its chosen credential.
public sealed record PasskeyLoginTarget(string CipherId, string Name, bool RequiresReprompt = false);

// Trusted, already-normalised request provenance derived from the Task 1 policy decision — never
// from page-reported values. Both the A-label (RpId/FrameOrigin/TopOrigin) and Unicode
// (UnicodeRpId) forms are supplied so the UI can present the canonical origin/RP verbatim.
public sealed record PasskeyApprovalSource(
    string FrameOrigin,
    string TopOrigin,
    string RpId,
    string UnicodeRpId,
    bool IsCrossOrigin);

public sealed record PasskeyApprovalRequest(
    string RequestId,
    PasskeyApprovalSource Source,
    IReadOnlyList<PasskeyCredentialTarget> Candidates,
    PasskeyUserVerificationRequirement UserVerificationRequirement);

public sealed record PasskeyCreateApprovalRequest(
    string RequestId,
    PasskeyApprovalSource Source,
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

