using Core.BrowserIntegration;
using Xunit;

namespace BrowserNativeHost.Tests;

/// <summary>
/// Pure decision that gates portable signer-pinning (Task 3 follow-up): which
/// <c>WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)</c> HRESULTs are acceptable for extracting
/// and pinning a peer's signer SPKI. Only a fully valid signature (<c>S_OK</c>) or a signature that
/// is intact and chain-complete but terminates in a root this machine hasn't been told to trust
/// (<c>CERT_E_UNTRUSTEDROOT</c>) may proceed; everything else (no signature, tampered digest,
/// expired, revoked, unknown subject, or any other status) is rejected.
/// </summary>
public sealed class AuthenticodeTrustDecisionTests
{
    [Fact]
    public void SOk_IsAcceptable() =>
        Assert.True(AuthenticodeTrustDecision.IsAcceptable(0));

    [Fact]
    public void CertUntrustedRoot_IsAcceptable() =>
        Assert.True(AuthenticodeTrustDecision.IsAcceptable(unchecked((int)0x800B0109)));

    [Theory]
    [InlineData(unchecked((int)0x800B0100))] // TRUST_E_NOSIGNATURE — file is not signed at all.
    [InlineData(unchecked((int)0x80096010))] // TRUST_E_BAD_DIGEST — file was tampered after signing.
    [InlineData(unchecked((int)0x800B0004))] // TRUST_E_SUBJECT_NOT_TRUSTED — subject not trusted.
    [InlineData(unchecked((int)0x800B0003))] // TRUST_E_SUBJECT_FORM_UNKNOWN — unknown subject form.
    [InlineData(unchecked((int)0x800B0001))] // TRUST_E_PROVIDER_UNKNOWN — no trust provider.
    [InlineData(unchecked((int)0x800B0101))] // CERT_E_EXPIRED — signer certificate expired.
    [InlineData(unchecked((int)0x800B010C))] // CERT_E_REVOKED — signer certificate revoked.
    [InlineData(unchecked((int)0x800B010A))] // CERT_E_CHAINING — incomplete chain, not merely untrusted root.
    [InlineData(unchecked((int)0x800B010D))] // CERT_E_UNTRUSTEDTESTROOT — test root, not exactly UNTRUSTEDROOT.
    [InlineData(unchecked((int)0x800B0110))] // CERT_E_WRONG_USAGE — wrong key usage.
    [InlineData(unchecked((int)0x80004005))] // E_FAIL — generic failure.
    [InlineData(1)]                          // arbitrary non-zero.
    [InlineData(int.MinValue)]
    public void OtherResults_AreRejected(int hresult) =>
        Assert.False(AuthenticodeTrustDecision.IsAcceptable(hresult));
}
