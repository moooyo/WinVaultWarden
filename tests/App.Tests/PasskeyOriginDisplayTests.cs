using App.Services;
using Xunit;

namespace App.Tests;

public class PasskeyOriginDisplayTests
{
    [Fact]
    public void Parse_HttpsOrigin_ReturnsSchemeAndHost_Secure()
    {
        var result = PasskeyOriginDisplay.Parse("https://github.com");

        Assert.Equal("https://github.com", result.Unicode);
        Assert.Equal("https://github.com", result.Ascii);
        Assert.True(result.IsSecure);
    }

    [Fact]
    public void Parse_HttpsWithPort_KeepsPort()
    {
        var result = PasskeyOriginDisplay.Parse("https://localhost:8443/ignored/path");

        Assert.Equal("https://localhost:8443", result.Unicode);
        Assert.Equal("https://localhost:8443", result.Ascii);
        Assert.True(result.IsSecure);
    }

    [Fact]
    public void Parse_HttpOrigin_IsNotSecure()
    {
        var result = PasskeyOriginDisplay.Parse("http://example.com");

        Assert.Equal("http://example.com", result.Unicode);
        Assert.Equal("http://example.com", result.Ascii);
        Assert.False(result.IsSecure);
    }

    [Fact]
    public void Parse_PunycodeHost_ExposesBothUnicodeAndAsciiForms()
    {
        // xn--mnchen-3ya == "münchen"; the A-label stays for verification, Unicode for readability.
        var result = PasskeyOriginDisplay.Parse("https://xn--mnchen-3ya.de");

        Assert.Equal("https://münchen.de", result.Unicode);
        Assert.Equal("https://xn--mnchen-3ya.de", result.Ascii);
        Assert.True(result.IsSecure);
    }

    [Fact]
    public void Parse_NullOrGarbage_ReturnsSanitizedPlaceholder_WithoutEchoingInput()
    {
        var nul = PasskeyOriginDisplay.Parse(null);
        Assert.Equal("(未知来源)", nul.Unicode);
        Assert.Equal("(未知来源)", nul.Ascii);
        Assert.False(nul.IsSecure);

        // Unparseable input must never be echoed back into the dialog verbatim.
        var garbage = PasskeyOriginDisplay.Parse("not a url");
        Assert.Equal("(未知来源)", garbage.Unicode);
        Assert.Equal("(未知来源)", garbage.Ascii);
        Assert.False(garbage.IsSecure);
    }
}
