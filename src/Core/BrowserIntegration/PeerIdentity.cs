namespace Core.BrowserIntegration;

/// <summary>
/// A verified snapshot of a named-pipe peer's Windows identity (Task 3). Both the App (pipe
/// server) and the native host (pipe client) build one for the local process and one for the
/// peer whose PID the kernel reports for the pipe, then compare them via <see cref="PeerIdentityPolicy"/>.
///
/// <para>Fields carry only non-sensitive identity facts. <c>PackageFamilyName</c> is null for a
/// portable (unpackaged) process; <c>AuthenticodeValid</c> is true when the binary's signature is
/// acceptable per <see cref="AuthenticodeTrustDecision"/> (fully valid, or self-signed with an
/// untrusted root); <c>SignerPublicKeySha256</c> is the lower-case hex SHA-256 of the Authenticode
/// signer's SubjectPublicKeyInfo and is null unless <c>AuthenticodeValid</c> is true.</para>
/// </summary>
public sealed record PeerIdentity(
    uint ProcessId,
    string UserSid,
    string ExecutablePath,
    string? PackageFamilyName,
    bool AuthenticodeValid,
    string? SignerPublicKeySha256);
