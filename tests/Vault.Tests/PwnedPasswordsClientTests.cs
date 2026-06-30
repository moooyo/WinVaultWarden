using System.Net;
using Vault;
using Xunit;

namespace Vault.Tests;

public class PwnedPasswordsClientTests
{
    // "password" SHA-1 = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8 → prefix 5BAA6, suffix 1E4C9B93F3F0682250B6CF8331B7EE68FD8
    [Fact]
    public async Task GetBreachCount_SendsPrefixOnly_AndParsesSuffixCount()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(req => { captured = req; return
            "0000000000000000000000000000000000A:5\r\n1E4C9B93F3F0682250B6CF8331B7EE68FD8:12345\r\n"; });
        var client = new PwnedPasswordsClient(new HttpClient(handler));

        var count = await client.GetBreachCountAsync("password", TestContext.Current.CancellationToken);

        Assert.Equal(12345, count);
        Assert.Equal("/range/5BAA6", captured!.RequestUri!.AbsolutePath);
        Assert.True(captured.Headers.Contains("Add-Padding"));
        // 绝不外发完整哈希或明文
        Assert.DoesNotContain("1E4C9B93", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetBreachCount_NoSuffixMatch_ReturnsZero()
    {
        var handler = new StubHandler(_ => "ABCDEF0000000000000000000000000000000:7\r\n");
        var client = new PwnedPasswordsClient(new HttpClient(handler));
        Assert.Equal(0, await client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBreachCount_EmptyPassword_ReturnsZeroWithNoNetworkCall()
    {
        var callCount = 0;
        var handler = new StubHandler(_ => { callCount++; return ""; });
        var client = new PwnedPasswordsClient(new HttpClient(handler));
        Assert.Equal(0, await client.GetBreachCountAsync("", TestContext.Current.CancellationToken));
        Assert.Equal(0, callCount);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _body;
        public StubHandler(Func<HttpRequestMessage, string> body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body(request)) });
    }
}
