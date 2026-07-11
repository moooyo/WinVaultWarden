namespace Core.BrowserIntegration;

/// <summary>
/// Pure, Win32-free decision that gates portable signer-pinning (Task 3). Both the App and the
/// native host verify a portable peer's binary with
/// <c>WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)</c> and pass the returned HRESULT here to
/// decide whether the signer public key may be extracted and pinned.
///
/// <para>Only two outcomes are acceptable:</para>
/// <list type="bullet">
///   <item><c>S_OK</c> — a fully valid signature that chains to a trusted root.</item>
///   <item><c>CERT_E_UNTRUSTEDROOT</c> — the signature is present, the digest matches, and a full
///   chain was built, but it terminates in a root this machine has not been told to trust. This is
///   exactly the portable case: we do <b>not</b> require the user to install the publisher
///   certificate, because identity is proven by matching signer <c>SubjectPublicKeyInfo</c> on both
///   ends (see <see cref="PeerIdentityPolicy"/>), not by chain trust.</item>
/// </list>
///
/// <para>Every other status is rejected fail-closed: <c>TRUST_E_NOSIGNATURE</c> (unsigned),
/// <c>TRUST_E_BAD_DIGEST</c> (tampered), unknown subject, expired, revoked, incomplete chain, test
/// root, or any other result. Rejecting means the peer is treated as unsigned and denied — this
/// deliberately does <b>not</b> widen to "any self-signed" or "has a certificate": the signer SPKI
/// must still match the other end.</para>
/// </summary>
public static class AuthenticodeTrustDecision
{
    /// <summary>S_OK — fully valid signature chaining to a trusted root.</summary>
    public const int Ok = 0;

    /// <summary>
    /// CERT_E_UNTRUSTEDROOT (0x800B0109) — intact, chain-complete signature whose root is not in
    /// this machine's trust store. Accepted because signer-pinning proves identity, not chain trust.
    /// </summary>
    public const int CertUntrustedRoot = unchecked((int)0x800B0109);

    /// <summary>
    /// True only for <see cref="Ok"/> or exactly <see cref="CertUntrustedRoot"/>. Any other
    /// WinVerifyTrust result (no signature, bad digest, unknown subject, expired, revoked, or any
    /// other status) returns false so the peer is treated as unsigned.
    /// </summary>
    public static bool IsAcceptable(int winVerifyTrustResult) =>
        winVerifyTrustResult == Ok || winVerifyTrustResult == CertUntrustedRoot;
}
