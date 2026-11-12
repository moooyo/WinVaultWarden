using Core.Models;
using System.Text.Json.Serialization;

namespace Core.Passkeys;

// Trusted request context supplied exclusively by the browser sender (extension +
// native host). The page never populates these fields; the policy derives origin and
// RP identity from them rather than from any page-reported payload.
public sealed record BrowserRequestContext(
    string FrameOrigin,
    string TopOrigin,
    int TabId,
    int FrameId,
    string DocumentId,
    bool PublicKeyCredentialsGetAllowed,
    [property: JsonRequired] bool SameOriginWithAncestors,
    [property: JsonRequired] bool PublicKeyCredentialsCreateAllowed);

public enum BrowserPasskeyOperation
{
    Create,
    Get,
}

// Result of a successful policy evaluation: normalised origins plus the canonical
// (A-label) and Unicode forms of the relying-party id, and whether the call is
// cross-origin relative to any ancestor, including an A-to-B-to-A frame chain.
public sealed record ValidatedBrowserRequestContext(
    ServerOrigin FrameOrigin,
    ServerOrigin TopOrigin,
    string CanonicalRpId,
    string UnicodeRpId,
    bool IsCrossOrigin);

// Discriminated outcome: either a validated context, or a stable, code-only error.
public readonly record struct WebAuthnPolicyDecision(
    ValidatedBrowserRequestContext? Context,
    string? ErrorCode)
{
    public bool Allowed => Context is not null;
}
