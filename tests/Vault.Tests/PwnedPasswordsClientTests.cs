using System.IO;
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
            new string('0', 34) + "A:5\r\n1E4C9B93F3F0682250B6CF8331B7EE68FD8:12345\r\n"; });
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
        var handler = new StubHandler(_ => "ABCDEF" + new string('0', 29) + ":7\r\n");
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

    [Fact]
    public async Task GetBreachCount_RateLimited_ThrowsWithStatus()
    {
        var client = new PwnedPasswordsClient(new HttpClient(
            new StatusHandler(HttpStatusCode.TooManyRequests)));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task GetBreachCount_CorruptedCount_ThrowsInvalidData()
    {
        // suffix 命中,但计数不是合法整数 → 绝不能悄悄返回 0。
        var handler = new StubHandler(_ => "1E4C9B93F3F0682250B6CF8331B7EE68FD8:not-a-number\r\n");
        var client = new PwnedPasswordsClient(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t\n")]
    [InlineData("<html>Service unavailable</html>")]
    [InlineData("ABCDEF:7")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ:7")]
    [InlineData("1E4C9B93F3F0682250B6CF8331B7EE68FD8:-1")]
    [InlineData("1E4C9B93F3F0682250B6CF8331B7EE68FD8:2147483648")]
    [InlineData("1E4C9B93F3F0682250B6CF8331B7EE68FD8:0:7")]
    public async Task GetBreachCount_EmptyOrMalformedSuccessBody_ThrowsWithoutSensitiveDetails(string body)
    {
        var client = new PwnedPasswordsClient(new HttpClient(new StubHandler(_ => body)));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));

        Assert.DoesNotContain("password", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1E4C9B93", error.Message, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(body))
            Assert.DoesNotContain(body, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBreachCount_ZeroCountPaddingAndNoMatch_ReturnsZero()
    {
        var body = new string('A', 35) + ":0\r\n" + new string('B', 35) + ":17\r\n";
        var client = new PwnedPasswordsClient(new HttpClient(new StubHandler(_ => body)));

        Assert.Equal(0, await client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBreachCount_MatchingZeroCountAndLowercaseSuffix_ReturnsZero()
    {
        var client = new PwnedPasswordsClient(new HttpClient(new StubHandler(_ =>
            "1e4c9b93f3f0682250b6cf8331b7ee68fd8:0\r\n")));

        Assert.Equal(0, await client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBreachCount_ValidRecordThenMalformedRecord_DoesNotReturnAnUncheckedCount()
    {
        var client = new PwnedPasswordsClient(new HttpClient(new StubHandler(_ =>
            "1E4C9B93F3F0682250B6CF8331B7EE68FD8:1\r\ninvalid\r\n")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetBreachCount_DuplicateMatchingSuffix_CannotOverwriteExposureWithZero()
    {
        var suffix = "1E4C9B93F3F0682250B6CF8331B7EE68FD8";
        var client = new PwnedPasswordsClient(new HttpClient(new StubHandler(_ =>
            $"{suffix}:9\r\n{suffix}:0\r\n")));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetBreachCountAsync("password", TestContext.Current.CancellationToken));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string> _body;
        public StubHandler(Func<HttpRequestMessage, string> body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body(request)) });
    }

    private sealed class StatusHandler(
        HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(string.Empty),
            });
    }
}
