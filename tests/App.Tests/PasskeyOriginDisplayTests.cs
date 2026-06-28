using App.Services;
using Xunit;

namespace App.Tests;

public class PasskeyOriginDisplayTests
{
    [Fact]
    public void Parse_HttpsOrigin_ReturnsSchemeAndHost_Secure()
    {
        var result = PasskeyOriginDisplay.Parse("https://github.com");

        Assert.Equal("https://github.com", result.Display);
        Assert.True(result.IsSecure);
    }

    [Fact]
    public void Parse_HttpsWithPort_KeepsPort()
    {
        var result = PasskeyOriginDisplay.Parse("https://localhost:8443/ignored/path");

        Assert.Equal("https://localhost:8443", result.Display);
        Assert.True(result.IsSecure);
    }

    [Fact]
    public void Parse_HttpOrigin_IsNotSecure()
    {
        var result = PasskeyOriginDisplay.Parse("http://example.com");

        Assert.Equal("http://example.com", result.Display);
        Assert.False(result.IsSecure);
    }

    [Fact]
    public void Parse_PunycodeHost_IsDecodedToUnicode()
    {
        // xn--mnchen-3ya == "münchen" ; use a stable IDN sample.
        var result = PasskeyOriginDisplay.Parse("https://xn--mnchen-3ya.de");

        Assert.Equal("https://münchen.de", result.Display);
        Assert.True(result.IsSecure);
    }

    [Fact]
    public void Parse_NullOrGarbage_ReturnsRawFallback_NotSecure()
    {
        Assert.Equal("(未知来源)", PasskeyOriginDisplay.Parse(null).Display);
        Assert.False(PasskeyOriginDisplay.Parse(null).IsSecure);

        var garbage = PasskeyOriginDisplay.Parse("not a url");
        Assert.Equal("not a url", garbage.Display);
        Assert.False(garbage.IsSecure);
    }
}
