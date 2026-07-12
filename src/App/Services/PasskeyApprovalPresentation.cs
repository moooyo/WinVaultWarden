using Core.Services;

namespace App.Services;

// WinUI-free view model for the passkey approval control. Everything it exposes is safe to show:
// the normalised frame/top origins and RP id in both A-label (unspoofable) and Unicode (readable)
// forms, the RP's self-reported name as a clearly-secondary hint, and account-picker metadata —
// never a password, private key or any other secret. The security-relevant provenance comes from
// the Task 1 validated context via PasskeyApprovalSource, not from page-reported values.
public sealed record PasskeyApprovalPresentation(
    string FrameOriginUnicode,
    string FrameOriginAscii,
    string TopOriginUnicode,
    string TopOriginAscii,
    string RpIdUnicode,
    string RpIdAscii,
    bool IsCrossOrigin,
    string? ReportedName,
    PasskeyCredentialTarget[] Candidates,
    PasskeyCredentialTarget? InitialSelection)
{
    public static PasskeyApprovalPresentation Build(
        PasskeyApprovalSource source,
        string? reportedRpName,
        IReadOnlyList<PasskeyCredentialTarget> candidates)
    {
        var materialized = candidates.ToArray();
        var frame = PasskeyOriginDisplay.Parse(source.FrameOrigin);
        var top = PasskeyOriginDisplay.Parse(source.TopOrigin);
        return new(
            frame.Unicode,
            frame.Ascii,
            top.Unicode,
            top.Ascii,
            source.UnicodeRpId,
            source.RpId,
            source.IsCrossOrigin,
            // The brand name is always a secondary line and can never replace the origin/RP identity.
            string.IsNullOrWhiteSpace(reportedRpName)
                ? null
                : $"网站自报名称：{reportedRpName}",
            materialized,
            // Only a lone credential may be pre-selected; multiple candidates force an explicit pick.
            materialized.Length == 1 ? materialized[0] : null);
    }
}
