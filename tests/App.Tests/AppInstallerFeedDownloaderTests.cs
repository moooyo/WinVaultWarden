using System.Net;
using System.Text;
using App.Services;
using Xunit;

namespace App.Tests;

public sealed class AppInstallerFeedDownloaderTests
{
    [Fact]
    public async Task Request_IsAnonymousAndStartsAtTheFixedArchitectureFeed()
    {
        using var downloader = new AppInstallerFeedDownloader(new Handler((request, _) =>
        {
            Assert.Equal("https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-arm64.appinstaller",
                request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(Content("metadata"));
        }));
        Assert.Equal("metadata", Encoding.UTF8.GetString(await downloader.DownloadAsync("arm64")));
    }

    [Fact]
    public async Task OfficialAssetRedirects_AreFollowedExplicitly()
    {
        var calls = 0;
        using var downloader = new AppInstallerFeedDownloader(new Handler((request, _) =>
        {
            calls++;
            return Task.FromResult(calls switch
            {
                1 => Redirect("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-x64.appinstaller"),
                2 => Redirect("https://release-assets.githubusercontent.com/github-production-release-asset/123/asset?token=public"),
                _ => Content("metadata"),
            });
        }));
        Assert.Equal("metadata", Encoding.UTF8.GetString(await downloader.DownloadAsync("x64")));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData("https://evil.example/WinVaultWarden-x64.appinstaller")]
    [InlineData("http://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-x64.appinstaller")]
    [InlineData("https://github.com/other/repo/releases/download/v0.2.0/WinVaultWarden-x64.appinstaller")]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v0.2.0/WinVaultWarden-arm64.appinstaller")]
    public async Task UntrustedRedirect_IsRejectedBeforeTheNextRequest(string location)
    {
        var calls = 0;
        using var downloader = new AppInstallerFeedDownloader(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Redirect(location));
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync("x64"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RedirectLoop_StopsAfterTheBoundedNumberOfRequests()
    {
        var calls = 0;
        using var downloader = new AppInstallerFeedDownloader(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Redirect("https://github.com/moooyo/WinVaultWarden/releases/latest/download/WinVaultWarden-x64.appinstaller"));
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync("x64"));
        Assert.Equal(5, calls);
    }

    [Fact]
    public async Task TransportCannotSilentlyChangeTheMetadataLocation()
    {
        using var downloader = new AppInstallerFeedDownloader(new Handler((_, _) =>
        {
            var response = Content("metadata");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/feed");
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync("x64"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task MissingOrUnavailableFeed_IsNotSuccessfulEnrollment(HttpStatusCode status)
    {
        using var downloader = new AppInstallerFeedDownloader(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync("x64"));
    }

    [Fact]
    public async Task UnadvertisedOversizedFeed_IsRejectedWhileReading()
    {
        using var downloader = new AppInstallerFeedDownloader(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[AppInstallerFeedPolicy.MaximumFeedBytes + 1])),
        })));
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync("x64"));
    }

    [Fact]
    public async Task TimeoutIsAFailureAndUserCancellationRemainsCancellation()
    {
        using var downloader = new AppInstallerFeedDownloader(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Content("metadata");
        }), TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync("x64"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync("x64", cancellation.Token));
    }

    [Fact]
    public async Task UnsupportedArchitecture_DoesNotStartARequest()
    {
        var calls = 0;
        using var downloader = new AppInstallerFeedDownloader(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Content("metadata"));
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync("x86"));
        Assert.Equal(0, calls);
    }

    private static HttpResponseMessage Content(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/octet-stream"),
    };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
