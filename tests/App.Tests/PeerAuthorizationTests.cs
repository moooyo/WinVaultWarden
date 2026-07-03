using App.Services;
using Xunit;

namespace App.Tests;

// M1 纯判定逻辑(镜像路径匹配 + 可选签名)单测。运行时取 PID/镜像路径须 app-run 验证。
public class PeerAuthorizationTests
{
    private const string Host = @"C:\Program Files\WinVaultWarden\BrowserNativeHost.exe";

    [Fact]
    public void MatchingPath_NoSignatureRequired_Authorized() =>
        Assert.True(PeerAuthorization.IsAuthorized(Host, Host, signatureValid: false, requireSignature: false));

    [Fact]
    public void DifferentPath_Rejected() =>
        Assert.False(PeerAuthorization.IsAuthorized(
            @"C:\Temp\evil.exe", Host, signatureValid: true, requireSignature: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmptyImagePath_Rejected(string? image) =>
        Assert.False(PeerAuthorization.IsAuthorized(image, Host, signatureValid: true, requireSignature: false));

    [Fact]
    public void EmptyExpectedPath_Rejected() =>
        Assert.False(PeerAuthorization.IsAuthorized(Host, "", signatureValid: true, requireSignature: false));

    [Fact]
    public void CaseInsensitivePathMatch_Authorized() =>
        Assert.True(PeerAuthorization.IsAuthorized(
            Host.ToUpperInvariant(), Host.ToLowerInvariant(), signatureValid: false, requireSignature: false));

    [Fact]
    public void NonNormalizedButEquivalentPath_Authorized() =>
        Assert.True(PeerAuthorization.IsAuthorized(
            @"C:\Program Files\WinVaultWarden\sub\..\BrowserNativeHost.exe", Host,
            signatureValid: false, requireSignature: false));

    [Fact]
    public void SignatureRequired_ButInvalid_Rejected() =>
        Assert.False(PeerAuthorization.IsAuthorized(Host, Host, signatureValid: false, requireSignature: true));

    [Fact]
    public void SignatureRequired_AndValid_PathMatches_Authorized() =>
        Assert.True(PeerAuthorization.IsAuthorized(Host, Host, signatureValid: true, requireSignature: true));
}
