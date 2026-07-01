using Core;
using Xunit;

namespace Vault.Tests;

public class IconDomainTests
{
    [Theory]
    [InlineData("https://www.example.com/login?x=1", "www.example.com")]
    [InlineData("http://example.com", "example.com")]
    [InlineData("example.com", "example.com")]              // 无 scheme → 补 https
    [InlineData("https://SUB.Example.COM", "sub.example.com")] // 归一小写
    [InlineData("http://1.2.3.4:8080/x", "1.2.3.4")]        // IP + 端口
    public void Extract_ReturnsHost(string uri, string expected) =>
        Assert.Equal(expected, IconDomain.Extract(uri));

    [Theory]
    [InlineData("androidapp://com.example.app")]
    [InlineData("iosapp://com.example.app")]
    [InlineData("ssh://host")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Extract_ReturnsNull_ForNonWebOrEmpty(string? uri) =>
        Assert.Null(IconDomain.Extract(uri));
}
