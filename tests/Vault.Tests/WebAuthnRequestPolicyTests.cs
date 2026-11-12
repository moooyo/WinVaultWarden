using Core.Passkeys;
using Xunit;

namespace Vault.Tests;

public sealed class WebAuthnRequestPolicyTests
{
    private readonly WebAuthnRequestPolicy _policy = WebAuthnRequestPolicy.CreateDefault();

    [Theory]
    [InlineData("https://login.example.com", "example.com")]
    [InlineData("http://localhost:8787", "localhost")]
    [InlineData("http://127.0.0.1:8787", "127.0.0.1")]
    [InlineData("http://[::1]:8787", "::1")]
    public void Get_AllowsSecureOrExactLoopback(string origin, string rpId)
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context(origin, origin, frameId: 0, policy: true),
            rpId);

        Assert.True(result.Allowed);
        Assert.Equal(rpId, result.Context!.CanonicalRpId);
    }

    [Theory]
    [InlineData("http://example.com", "example.com")]
    [InlineData("http://localhost.evil.test", "evil.test")]
    [InlineData("http://127.0.0.2", "127.0.0.2")]
    [InlineData("file:///C:/site/index.html", "site")]
    public void Get_RejectsInsecureOrigin(string origin, string rpId)
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context(origin, origin, 0, true),
            rpId);

        Assert.Equal(PasskeyErrorCodes.SecurityInsecureOrigin, result.ErrorCode);
    }

    [Theory]
    [InlineData("https://example.com", "com")]
    [InlineData("https://login.example.co.uk", "co.uk")]
    [InlineData("https://a.foo.ck", "foo.ck")]
    public void Get_RejectsPublicSuffix(string origin, string rpId)
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context(origin, origin, 0, true),
            rpId);

        Assert.Equal(PasskeyErrorCodes.SecurityPublicSuffix, result.ErrorCode);
    }

    [Fact]
    public void Get_HonorsPslExceptionRule()
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context("https://www.ck", "https://www.ck", 0, true),
            "www.ck");

        Assert.True(result.Allowed);
    }

    [Theory]
    [InlineData("https://login.example.com", "ample.com")]
    [InlineData("https://127.0.0.1", "0.0.1")]
    [InlineData("https://xn--mnchen-3ya.de", "xn--MNCHEN-3ya.de")]
    [InlineData("https://xn--mnchen-3ya.de", "xn--invalid-.de")]
    [InlineData("https://example.com", "example.com.")]
    public void Get_RejectsForeignOrNonCanonicalRp(string origin, string rpId)
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context(origin, origin, 0, true),
            rpId);

        Assert.Contains(result.ErrorCode, new[]
        {
            PasskeyErrorCodes.SecurityInvalidRp,
            PasskeyErrorCodes.SecurityIdn,
        });
    }

    [Fact]
    public void Get_ReturnsUnicodeAndPunycodeForms()
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context("https://xn--mnchen-3ya.de", "https://xn--mnchen-3ya.de", 0, true),
            "xn--mnchen-3ya.de");

        Assert.Equal("xn--mnchen-3ya.de", result.Context!.CanonicalRpId);
        Assert.Equal("münchen.de", result.Context.UnicodeRpId);
    }

    [Fact]
    public void Create_RejectsIframe()
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Create,
            Context("https://login.example.com", "https://example.com", 7, true),
            "example.com");

        Assert.Equal(PasskeyErrorCodes.SecurityFrame, result.ErrorCode);
    }

    [Fact]
    public void CrossOriginGet_RequiresPermissionsPolicy()
    {
        var denied = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context("https://login.example.com", "https://example.com", 7, false),
            "example.com");
        var allowed = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context("https://login.example.com", "https://example.com", 7, true),
            "example.com");

        Assert.Equal(PasskeyErrorCodes.SecurityPermissionsPolicy, denied.ErrorCode);
        Assert.True(allowed.Allowed);
        Assert.True(allowed.Context!.IsCrossOrigin);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void SameOriginGet_RejectsExplicitPermissionsPolicyDenial(int frameId)
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context("https://example.com", "https://example.com", frameId, false),
            "example.com");

        Assert.False(result.Allowed);
        Assert.Equal(PasskeyErrorCodes.SecurityPermissionsPolicy, result.ErrorCode);
    }

    [Fact]
    public void Create_RejectsTopLevelPermissionsPolicyDenial()
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Create,
            Context("https://example.com", "https://example.com", 0, true) with
            {
                PublicKeyCredentialsCreateAllowed = false,
            },
            "example.com");

        Assert.False(result.Allowed);
        Assert.Equal(PasskeyErrorCodes.SecurityPermissionsPolicy, result.ErrorCode);
    }

    [Fact]
    public void Get_UsesFullAncestorEvidenceWhenFrameAndTopOriginsMatch()
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context("https://example.com", "https://example.com", 7, true) with
            {
                SameOriginWithAncestors = false,
            },
            "example.com");

        Assert.True(result.Allowed);
        Assert.True(result.Context!.IsCrossOrigin);
    }

    [Theory]
    [InlineData(0, "https://example.com", "https://example.com", false)]
    [InlineData(0, "https://example.com", "https://other.example", false)]
    [InlineData(7, "https://example.com", "https://other.example", true)]
    public void Get_RejectsInconsistentTrustedAncestorEvidence(
        int frameId, string frame, string top, bool sameOriginWithAncestors)
    {
        var result = _policy.Evaluate(
            BrowserPasskeyOperation.Get,
            Context(frame, top, frameId, true) with
            {
                SameOriginWithAncestors = sameOriginWithAncestors,
            },
            "example.com");

        Assert.False(result.Allowed);
        Assert.Equal(PasskeyErrorCodes.SecuritySender, result.ErrorCode);
    }

    private static BrowserRequestContext Context(
        string frame,
        string top,
        int frameId,
        bool policy) =>
        new(frame, top, 42, frameId, "document-1", policy,
            SameOriginWithAncestors: frame == top,
            PublicKeyCredentialsCreateAllowed: true);
}
