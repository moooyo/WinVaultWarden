using Api;
using Xunit;

namespace Api.Tests;

/// <summary>
/// Unit tests for NotificationsConnection.ToHubUrl static helper.
/// Only URL construction is tested here; live socket behaviour is covered by LiveSmoke.
/// </summary>
public class WebSocketUrlTests
{
    // ── http → ws ──────────────────────────────────────────────────────────────

    [Fact]
    public void Http_becomes_ws_with_token()
    {
        Assert.Equal(
            "ws://10.0.1.20:8080/notifications/hub?access_token=TKN",
            NotificationsConnection.ToHubUrl("http://10.0.1.20:8080", "TKN"));
    }

    [Fact]
    public void Http_with_standard_port_80_is_preserved()
    {
        // port 80 is default for http, but the test-env uses 8080 so ensure
        // that a non-default port is always preserved.
        var url = NotificationsConnection.ToHubUrl("http://vault.example.com:8080", "ABC");
        Assert.StartsWith("ws://vault.example.com:8080/", url);
    }

    // ── https → wss ────────────────────────────────────────────────────────────

    [Fact]
    public void Https_becomes_wss()
    {
        Assert.StartsWith("wss://", NotificationsConnection.ToHubUrl("https://vault.example.com", "x"));
    }

    [Fact]
    public void Https_with_non_default_port_is_preserved()
    {
        var url = NotificationsConnection.ToHubUrl("https://vault.example.com:9443", "TK");
        Assert.Equal("wss://vault.example.com:9443/notifications/hub?access_token=TK", url);
    }

    // ── path & query ───────────────────────────────────────────────────────────

    [Fact]
    public void Path_is_always_notifications_hub()
    {
        var url = NotificationsConnection.ToHubUrl("http://10.0.1.20:8080", "x");
        var uri = new Uri(url);
        Assert.Equal("/notifications/hub", uri.AbsolutePath);
    }

    [Fact]
    public void Token_is_percent_encoded()
    {
        // access tokens contain '+', '/' and '=' which must be percent-encoded
        var url = NotificationsConnection.ToHubUrl("http://localhost:8080", "a+b/c=");
        Assert.Contains("access_token=a%2Bb%2Fc%3D", url);
    }
}
