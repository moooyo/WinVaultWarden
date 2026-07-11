using Core.BrowserIntegration;
using Xunit;

namespace BrowserNativeHost.Tests;

public sealed class PeerIdentityPolicyTests
{
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string App = @"C:\Program Files\WinVaultWarden\App.exe";
    private const string Host = @"C:\Program Files\WinVaultWarden\BrowserNativeHost.exe";
    private const string Signer = "06e6f70d4fcb988b27e63b2d3f833f4ab02e36c4b87c46447357674989bd2636";

    [Fact]
    public void SamePackageSidAndExpectedPath_IsAllowed()
    {
        var local = Identity(10, App, package: "WinVaultWarden_abc", signer: null);
        var peer = Identity(20, Host, package: "WinVaultWarden_abc", signer: null);
        Assert.True(PeerIdentityPolicy.IsAuthorized(local, peer, Host));
    }

    [Fact]
    public void PortableRequiresValidMatchingSigner()
    {
        var local = Identity(10, App, package: null, signer: Signer);
        var peer = Identity(20, Host, package: null, signer: Signer);
        Assert.True(PeerIdentityPolicy.IsAuthorized(local, peer, Host));

        Assert.False(PeerIdentityPolicy.IsAuthorized(
            local,
            peer with { AuthenticodeValid = false },
            Host));
        Assert.False(PeerIdentityPolicy.IsAuthorized(
            local,
            peer with { SignerPublicKeySha256 = new string('0', 64) },
            Host));
    }

    [Fact]
    public void PortableRejectsWhenLocalSignatureInvalid()
    {
        var local = Identity(10, App, package: null, signer: Signer) with { AuthenticodeValid = false };
        var peer = Identity(20, Host, package: null, signer: Signer);
        Assert.False(PeerIdentityPolicy.IsAuthorized(local, peer, Host));
    }

    [Fact]
    public void NonNormalizedButEquivalentPeerPath_IsAllowed()
    {
        var local = Identity(10, App, package: null, signer: Signer);
        var peer = Identity(
            20,
            @"C:\Program Files\WinVaultWarden\sub\..\BrowserNativeHost.exe",
            package: null,
            signer: Signer);
        Assert.True(PeerIdentityPolicy.IsAuthorized(local, peer, Host));
    }

    [Theory]
    [InlineData("S-1-5-21-evil", Host, "WinVaultWarden_abc")]
    [InlineData(Sid, @"C:\Temp\BrowserNativeHost.exe", "WinVaultWarden_abc")]
    [InlineData(Sid, Host, "OtherPackage_abc")]
    [InlineData(Sid, Host, null)]
    public void MismatchedSidPathOrPackage_IsRejected(
        string sid,
        string path,
        string? package)
    {
        var local = Identity(10, App, "WinVaultWarden_abc", null);
        var peer = Identity(20, path, package, null) with { UserSid = sid };
        Assert.False(PeerIdentityPolicy.IsAuthorized(local, peer, Host));
    }

    private static PeerIdentity Identity(
        uint pid,
        string path,
        string? package,
        string? signer) =>
        new(pid, Sid, path, package, signer is not null, signer);
}
