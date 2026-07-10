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
    [InlineData("******vault.example")]
    public void Constructor_RejectsUnsafeValues(string input) =>
        Assert.Throws<ArgumentException>(() => new ServerOrigin(input));

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
}
