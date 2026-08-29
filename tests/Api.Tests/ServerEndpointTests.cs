using Core.Models;
using Xunit;

namespace Api.Tests;

public sealed class ServerEndpointTests
{
    [Theory]
    [InlineData("HTTPS://Example.COM:443/vault/api/", "https://example.com/vault/api", "/vault/api")]
    [InlineData("http://EXAMPLE.com:80/", "http://example.com", "")]
    [InlineData("https://XN--BCHER-KVA.example/base", "https://xn--bcher-kva.example/base", "/base")]
    public void Constructor_NormalizesAuthorityAndPreservesPath(
        string input,
        string expectedValue,
        string expectedBasePath)
    {
        var endpoint = new ServerEndpoint(input);

        Assert.Equal(expectedValue, endpoint.Value);
        Assert.Equal(expectedBasePath, endpoint.BasePath);
        Assert.Equal(new ServerOrigin(expectedValue), endpoint.Origin);
    }

    [Theory]
    [InlineData("vault.example/api")]
    [InlineData("ftp://vault.example/api")]
    [InlineData("https://vault.example/api?mode=test")]
    [InlineData("https://vault.example/api#section")]
    [InlineData("https://vault.example/api?")]
    [InlineData("https://vault.example/api#")]
    [InlineData("https://vault.example/api%2Fadmin")]
    [InlineData("https://vault.example/base/../api")]
    [InlineData("https://vault.example/base/%2e%2e/api")]
    [InlineData("https://vault.example/base/%2E/api")]
    [InlineData(@"https://vault.example\api")]
    public void Constructor_RejectsUnsafeValues(string input) =>
        Assert.Throws<ArgumentException>(() => new ServerEndpoint(input));

    [Fact]
    public void Constructor_RejectsUserInfo()
    {
        var input = string.Concat("https://", "user", ":", "secret", "@", "vault.example/api");

        Assert.Throws<ArgumentException>(() => new ServerEndpoint(input));
    }

    [Fact]
    public void Matches_UsesOriginAndPathSegmentBoundary()
    {
        var endpoint = new ServerEndpoint("https://vault.example/base/api");

        Assert.True(endpoint.Matches(new Uri("https://vault.example/base/api")));
        Assert.True(endpoint.Matches(new Uri("https://vault.example/base/api/")));
        Assert.True(endpoint.Matches(new Uri("https://vault.example/base/api/sync?full=true")));
        Assert.False(endpoint.Matches(new Uri("https://vault.example/base/apiary")));
        Assert.False(endpoint.Matches(new Uri("https://vault.example/base")));
        Assert.False(endpoint.Matches(new Uri("https://vault.example/Base/api")));
        Assert.False(endpoint.Matches(new Uri("https://other.example/base/api")));
    }

    [Fact]
    public void Resolve_AppendsWithinBasePath()
    {
        var endpoint = new ServerEndpoint("https://vault.example/base/api/");

        Assert.Equal(
            "https://vault.example/base/api/sync?excludeDomains=true",
            endpoint.Resolve("sync?excludeDomains=true").AbsoluteUri);
        Assert.Equal(
            "https://vault.example/base/api?full=true",
            endpoint.Resolve("?full=true").AbsoluteUri);
        Assert.Equal(
            "https://vault.example/base/api/sync?next=../identity",
            endpoint.Resolve("sync?next=../identity").AbsoluteUri);
    }

    [Fact]
    public void Resolve_AcceptsAbsoluteUriOnlyInsideEndpointBoundary()
    {
        var endpoint = new ServerEndpoint("https://vault.example/base/api");

        var result = endpoint.Resolve("https://vault.example/base/api/sync?full=true");

        Assert.Equal("https://vault.example/base/api/sync?full=true", result.AbsoluteUri);
    }

    [Theory]
    [InlineData("../identity/connect/token")]
    [InlineData("/identity/connect/token")]
    [InlineData("//evil.example/steal")]
    [InlineData(@"..\identity")]
    [InlineData("https://vault.example/base/identity")]
    [InlineData("https://other.example/base/api")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https:evil.example")]
    [InlineData("sync#fragment")]
    [InlineData("ciphers/../sync")]
    [InlineData("./sync")]
    [InlineData("ciphers/%2e%2e/sync")]
    [InlineData("%2E/sync")]
    [InlineData("ciphers/%2Fadmin")]
    [InlineData("ciphers/%5cadmin")]
    [InlineData("https://vault.example/base/api/ciphers/%2e%2e/sync")]
    public void Resolve_RejectsEscapesAndUnsafeReferences(string input)
    {
        var endpoint = new ServerEndpoint("https://vault.example/base/api");

        var exception = Assert.Throws<ArgumentException>(() => endpoint.Resolve(input));

        Assert.Equal("relativeOrAbsolute", exception.ParamName);
    }

    [Fact]
    public void Append_CreatesChildEndpoint()
    {
        var endpoint = new ServerEndpoint("https://vault.example/base");

        var child = endpoint.Append("api/accounts");

        Assert.Equal("https://vault.example/base/api/accounts", child.Value);
    }

    [Theory]
    [InlineData("../api")]
    [InlineData("/api")]
    [InlineData("https://vault.example/base/api")]
    [InlineData("api?mode=test")]
    [InlineData("api#fragment")]
    [InlineData("api/..")]
    [InlineData(".")]
    [InlineData("api/%2e%2e")]
    [InlineData("api/%2Fadmin")]
    public void Append_RejectsNonChildEndpointPaths(string input)
    {
        var endpoint = new ServerEndpoint("https://vault.example/base");

        var exception = Assert.Throws<ArgumentException>(() => endpoint.Append(input));

        Assert.Equal("relativePath", exception.ParamName);
    }
}

public sealed class ServerEndpointsTests
{
    [Fact]
    public void BitwardenUs_UsesIndependentOfficialHosts()
    {
        var endpoints = ServerEndpoints.BitwardenUs;

        Assert.Equal("https://vault.bitwarden.com", endpoints.WebVault.Value);
        Assert.Equal("https://api.bitwarden.com", endpoints.Api.Value);
        Assert.Equal("https://identity.bitwarden.com", endpoints.Identity.Value);
        Assert.Equal("https://notifications.bitwarden.com", endpoints.Notifications.Value);
        Assert.Equal("https://icons.bitwarden.net", endpoints.Icons.Value);
        Assert.Equal("https://send.bitwarden.com", endpoints.SendWeb.Value);
    }

    [Fact]
    public void BitwardenEu_UsesIndependentOfficialHosts()
    {
        var endpoints = ServerEndpoints.BitwardenEu;

        Assert.Equal("https://vault.bitwarden.eu", endpoints.WebVault.Value);
        Assert.Equal("https://api.bitwarden.eu", endpoints.Api.Value);
        Assert.Equal("https://identity.bitwarden.eu", endpoints.Identity.Value);
        Assert.Equal("https://notifications.bitwarden.eu", endpoints.Notifications.Value);
        Assert.Equal("https://icons.bitwarden.eu", endpoints.Icons.Value);
        Assert.Equal("https://vault.bitwarden.eu", endpoints.SendWeb.Value);
    }

    [Fact]
    public void ForSelfHosted_PreservesWebVaultSubpathForEveryService()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example:8443/passwords/");

        Assert.Equal("https://vault.example:8443/passwords", endpoints.WebVault.Value);
        Assert.Equal("https://vault.example:8443/passwords/api", endpoints.Api.Value);
        Assert.Equal("https://vault.example:8443/passwords/identity", endpoints.Identity.Value);
        Assert.Equal("https://vault.example:8443/passwords/notifications", endpoints.Notifications.Value);
        Assert.Equal("https://vault.example:8443/passwords/icons", endpoints.Icons.Value);
        Assert.Equal(endpoints.WebVault, endpoints.SendWeb);
    }

    [Theory]
    [InlineData("https://vault.bitwarden.com/", "https://identity.bitwarden.com")]
    [InlineData("https://send.bitwarden.com", "https://identity.bitwarden.com")]
    [InlineData("https://vault.bitwarden.eu", "https://identity.bitwarden.eu")]
    public void FromWebVault_RecognizesOfficialCatalogs(string webVault, string expectedIdentity)
    {
        var endpoints = ServerEndpoints.FromWebVault(webVault);

        Assert.Equal(expectedIdentity, endpoints.Identity.Value);
    }

    [Fact]
    public void FromWebVault_TreatsOfficialHostSubpathAsSelfHosted()
    {
        var endpoints = ServerEndpoints.FromWebVault("https://vault.bitwarden.com/custom");

        Assert.Equal("https://vault.bitwarden.com/custom/api", endpoints.Api.Value);
        Assert.Equal("https://vault.bitwarden.com/custom/identity", endpoints.Identity.Value);
    }

    [Fact]
    public void Constructor_RejectsDefaultEndpoints()
    {
        Assert.Throws<ArgumentException>(() => new ServerEndpoints(
            default,
            new ServerEndpoint("https://api.example"),
            new ServerEndpoint("https://identity.example"),
            new ServerEndpoint("https://notifications.example"),
            new ServerEndpoint("https://icons.example"),
            new ServerEndpoint("https://send.example")));
    }
}
