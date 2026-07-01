using System.Net;
using System.Net.Http.Headers;
using Core.Models;
using Vault;
using Xunit;

namespace Vault.Tests;

public class FaviconCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wvw-icons-" + Guid.NewGuid().ToString("N"));

    private VaultSession Session(string serverUrl = "https://vault.example")
    {
        var s = new VaultSession();
        s.SetAccount(new AccountInfo("e@x.com", serverUrl, "E", "kdf"));
        return s;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public int Calls { get; private set; }
        public List<HttpRequestMessage> Requests { get; } = new();
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        { Calls++; Requests.Add(r); return Task.FromResult(_fn(r)); }
    }

    private static HttpResponseMessage Png(byte[] bytes)
    {
        var m = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        m.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return m;
    }

    [Fact]
    public async Task GetAsync_Miss_FetchesWritesAndReturns_ThenHitsDiskNoRequest()
    {
        var png = new byte[] { 1, 2, 3, 4 };
        var handler = new StubHandler(_ => Png(png));
        var cache = new FaviconCache(new HttpClient(handler), Session(), _dir);

        var first = await cache.GetAsync("example.com", TestContext.Current.CancellationToken);
        Assert.Equal(png, first);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("/icons/example.com/icon.png", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Null(handler.Requests[0].Headers.Authorization);

        var second = await cache.GetAsync("example.com", TestContext.Current.CancellationToken);
        Assert.Equal(png, second);
        Assert.Equal(1, handler.Calls); // 命中磁盘,不再请求
    }

    [Fact]
    public async Task GetAsync_NonImage_ReturnsNull_AndNegativeCaches()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cache = new FaviconCache(new HttpClient(handler), Session(), _dir);

        Assert.Null(await cache.GetAsync("nope.com", TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetAsync("nope.com", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls); // 负缓存命中,不再请求
    }

    [Fact]
    public async Task GetAsync_FetchThrows_WithStaleCache_ReturnsStale()
    {
        var png = new byte[] { 9, 9 };
        var ok = new StubHandler(_ => Png(png));
        var c1 = new FaviconCache(new HttpClient(ok), Session(), _dir);
        await c1.GetAsync("stale.com", TestContext.Current.CancellationToken); // 落盘

        // 令缓存过期:把 .png mtime 改到 40 天前
        var file = Directory.EnumerateFiles(_dir, "*.png").First();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-40));

        var boom = new StubHandler(_ => throw new HttpRequestException("network"));
        var c2 = new FaviconCache(new HttpClient(boom), Session(), _dir);
        Assert.Equal(png, await c2.GetAsync("stale.com", TestContext.Current.CancellationToken)); // 用旧缓存
    }

    [Fact]
    public async Task GetAsync_NoServerUrl_ReturnsNull_NoRequest()
    {
        var handler = new StubHandler(_ => Png(new byte[] { 1 }));
        var cache = new FaviconCache(new HttpClient(handler), Session(serverUrl: ""), _dir);
        Assert.Null(await cache.GetAsync("x.com", TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public GatedHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        public void Release() => _gate.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await _gate.Task; // 卡住共享抓取,直到测试放行,确保 WaitAsync 观察到的是"未完成"的任务
            return _fn(r);
        }
    }

    [Fact]
    public async Task GetAsync_CancelledToken_ReturnsNull_DoesNotThrow()
    {
        // 用尚未完成的共享任务+ 已取消的 token,验证 WaitAsync 会在调用方这一侧抛出并被吞掉(绝不抛),
        // 而不是像同步立即完成的 stub 那样让取消令牌被"竞速"绕过。
        var handler = new GatedHandler(_ => Png(new byte[] { 1 }));
        var cache = new FaviconCache(new HttpClient(handler), Session(), _dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = cache.GetAsync("example.com", cts.Token); // must NOT throw
        handler.Release(); // 让共享抓取完成,避免测试进程遗留悬挂任务
        var result = await task;

        Assert.Null(result);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
