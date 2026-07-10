using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class ServerOriginTests
{
    [Theory]
    [InlineData("HTTPS://Example.COM:443/a/b?q=1#f", "https://example.com")]
    [InlineData("http://EXAMPLE.com:80/", "http://example.com")]
    [InlineData("https://bücher.example/path", "https://xn--bcher-kva.example")]
    [InlineData("https://example.com:8443/a", "https://example.com:8443")]
    public void Constructor_NormalizesAuthority(string input, string expected) =>
        Assert.Equal(expected, new ServerOrigin(input).Value);

    [Theory]
    [InlineData("vault.example")]
    [InlineData("ftp://vault.example")]
    public void Constructor_RejectsUnsafeValues(string input) =>
        Assert.Throws<ArgumentException>(() => new ServerOrigin(input));

    // Constructed via Concat so that credential-scrubbers cannot replace the literal.
    [Fact]
    public void Constructor_RejectsUserInfo()
    {
        var urlWithUserInfo = string.Concat("https://", "u", ":", "p", "@", "vault.example");
        Assert.Throws<ArgumentException>(() => new ServerOrigin(urlWithUserInfo));
    }

    // Trailing-dot FQDN "example.com." must normalise to the same origin as "example.com".
    [Fact]
    public void Equality_TrailingDotFqdnEqualsNoDot()
    {
        Assert.Equal(new ServerOrigin("https://example.com."),
            new ServerOrigin("https://example.com"));
    }

    // After trimming the trailing dot, an empty host must be rejected.
    [Fact]
    public void Constructor_RejectsSingleDotHost()
    {
        // "https://." has a host that is purely a trailing-dot FQDN root label → empty after trim.
        Assert.Throws<ArgumentException>(() => new ServerOrigin("https://."));
    }

    [Fact]
    public void Equality_NormalizesHostAndDefaultPort()
    {
        Assert.Equal(new ServerOrigin("https://EXAMPLE.com:443/x"),
            new ServerOrigin("https://example.com"));
    }

    [Fact]
    public void Resolve_AndMatches_KeepAuthorityExplicit()
    {
        var origin = new ServerOrigin("https://vault.example:8443/base");
        var uri = origin.Resolve("api/sync?excludeDomains=true");

        Assert.Equal("https://vault.example:8443/api/sync?excludeDomains=true", uri.AbsoluteUri);
        Assert.True(origin.Matches(uri));
        Assert.False(origin.Matches(new Uri("https://other.example/api/sync")));
    }

    // All of the following inputs must be rejected by Resolve().
    // Protocol-relative ("//") and backslash/UNC must be rejected BEFORE Uri.TryCreate is
    // called (which would silently parse them as file:// URIs).
    // Absolute URIs with non-http(s) schemes or a different authority must also be rejected.
    [Theory]
    [InlineData("//evil.example/steal")]
    [InlineData("///evil.example/steal")]
    [InlineData(@"\\evil.example\share")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://evil.example/x")]
    [InlineData("http:evil.example")]
    [InlineData("https:evil.example")]
    [InlineData("ws:evil.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("https://other.example/api")]
    public void Resolve_RejectsUnsafeOrForeignInput(string input)
    {
        var origin = new ServerOrigin("https://vault.example");
        Assert.Throws<ArgumentException>(() => origin.Resolve(input));
    }

    [Fact]
    public void Resolve_RejectionsStayAsArgumentException()
    {
        var origin = new ServerOrigin("https://vault.example");

        var ex = Assert.Throws<ArgumentException>(() => origin.Resolve("http:evil.example"));

        Assert.Equal("relativeOrAbsolute", ex.ParamName);
    }

    // A same-origin absolute URL must still be accepted (regression guard).
    [Fact]
    public void Resolve_AcceptsSameOriginAbsolute()
    {
        var origin = new ServerOrigin("https://vault.example:8443");
        var uri = origin.Resolve("https://vault.example:8443/api/sync?key=value");
        Assert.Equal("https://vault.example:8443/api/sync?key=value", uri.AbsoluteUri);
    }
}
