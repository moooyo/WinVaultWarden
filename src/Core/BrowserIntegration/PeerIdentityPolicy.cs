using System.Security.Cryptography;
using System.Text;

namespace Core.BrowserIntegration;

/// <summary>
/// Pure, Win32-free authorization decision for a named-pipe peer (Task 3). Kept side-effect free
/// so every SID / path / package / signer combination is exhaustively unit-testable.
///
/// <para>Invariant (fail-closed): the peer is authorized only when</para>
/// <list type="number">
///   <item>its user SID equals the local process SID (same interactive user), and</item>
///   <item>its normalized executable path equals the portable locator's expected path, and</item>
///   <item>either both sides carry the same package family (MSIX), or both sides are portable
///   with acceptable Authenticode signatures (fully valid, or self-signed with an untrusted root —
///   see <see cref="AuthenticodeTrustDecision"/>) whose signer public keys hash-match.</item>
/// </list>
/// <para>A peer with a package where the local process has none (or vice versa) is rejected outright.</para>
/// </summary>
public static class PeerIdentityPolicy
{
    public static bool IsAuthorized(
        PeerIdentity local,
        PeerIdentity peer,
        string expectedExecutablePath)
    {
        if (!string.Equals(local.UserSid, peer.UserSid, StringComparison.Ordinal)
            || !PathsEqual(peer.ExecutablePath, expectedExecutablePath))
            return false;

        var localPackaged = !string.IsNullOrWhiteSpace(local.PackageFamilyName);
        var peerPackaged = !string.IsNullOrWhiteSpace(peer.PackageFamilyName);
        if (localPackaged || peerPackaged)
        {
            return localPackaged
                && peerPackaged
                && string.Equals(
                    local.PackageFamilyName,
                    peer.PackageFamilyName,
                    StringComparison.Ordinal);
        }

        return local.AuthenticodeValid
            && peer.AuthenticodeValid
            && FixedEquals(local.SignerPublicKeySha256, peer.SignerPublicKeySha256);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool FixedEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != 64 || right.Length != 64)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }
}
