using System.Net;
using System.Text;
using System.Text.Json;
using App.Services;
using Xunit;

namespace App.Tests;

public sealed class ReleaseUpdateServiceTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(-25, true)]
    [InlineData(-24, true)]
    [InlineData(-23, false)]
    [InlineData(0, false)]
    [InlineData(24, true)]
    public void AutomaticCheck_UsesDailyAttemptBudgetAndRecoversFromFutureClock(int? hours, bool expected)
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, ReleaseUpdateService.ShouldAutomaticallyCheck(now, hours is null ? null : now.AddHours(hours.Value)));
    }

    [Theory]
    [InlineData("x64", false, "WinVaultWarden-1.2.0-x64-portable.zip")]
    [InlineData("arm64", false, "WinVaultWarden-1.2.0-arm64-portable.zip")]
    [InlineData("x64", true, "WinVaultWarden-1.2.0-x64.msix")]
    [InlineData("arm64", true, "WinVaultWarden-1.2.0-arm64.msix")]
    public async Task NewerStableRelease_SelectsExactDistribution(string architecture, bool packaged, string expected)
    {
        using var service = Service(Json());
        var result = await service.CheckAsync("1.1.0.0", architecture, packaged);
        Assert.Equal(ReleaseUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(expected, result.Asset!.Name);
        Assert.StartsWith("https://github.com/moooyo/WinVaultWarden/releases/download/v1.2.0/", result.Asset.DownloadUrl.AbsoluteUri);
        Assert.Equal(new string('a', 64), result.Asset.Sha256);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    [InlineData("1.2.0+build.1")]
    public async Task EqualOrNewerInstallation_DoesNotOfferDowngrade(string current)
    {
        using var service = Service(Json());
        var result = await service.CheckAsync(current, "x64", false);
        Assert.Equal(ReleaseUpdateStatus.UpToDate, result.Status);
        Assert.Null(result.Asset);
    }

    [Fact]
    public async Task PrereleaseInstallation_OffersMatchingStableRelease()
    {
        using var service = Service(Json());
        Assert.Equal(ReleaseUpdateStatus.UpdateAvailable,
            (await service.CheckAsync("1.2.0-rc.2", "x64", false)).Status);
    }

    [Fact]
    public async Task NullAssetEntries_DoNotHideAValidCompatiblePackage()
    {
        using var service = Service(Json().Replace("\"assets\":[", "\"assets\":[null,", StringComparison.Ordinal));
        var result = await service.CheckAsync("1.0.0", "x64", false);
        Assert.Equal(ReleaseUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("WinVaultWarden-1.2.0-x64-portable.zip", result.Asset!.Name);
    }

    [Fact]
    public async Task OnlyNullAssets_ReportNoCompatiblePackage()
    {
        using var service = Service("""
            {"tag_name":"v1.2.0","html_url":"https://github.com/moooyo/WinVaultWarden/releases/tag/v1.2.0","draft":false,"prerelease":false,"assets":[null]}
            """);
        var result = await service.CheckAsync("1.0.0", "x64", false);
        Assert.Equal(ReleaseUpdateStatus.Unavailable, result.Status);
        Assert.Null(result.Asset);
    }

    [Theory]
    [InlineData("01.1.0")]
    [InlineData("1.2")]
    [InlineData("1.2.0.1")]
    [InlineData("1.2.0-01")]
    [InlineData("1.2.0+bad/key")]
    public async Task InvalidCurrentVersion_DoesNotMakeNetworkRequest(string current)
    {
        var calls = 0;
        using var service = new ReleaseUpdateService(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Response(Json()));
        }));
        Assert.Equal(ReleaseUpdateStatus.Failed, (await service.CheckAsync(current, "x64", false)).Status);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://evil.example/moooyo/WinVaultWarden/releases/tag/v1.2.0")]
    [InlineData("https://github.com/other/WinVaultWarden/releases/tag/v1.2.0")]
    [InlineData("http://github.com/moooyo/WinVaultWarden/releases/tag/v1.2.0")]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/tag/v1.2.0?redirect=evil")]
    public async Task UntrustedReleasePage_IsRejected(string page)
    {
        using var service = Service(Json(page: page));
        Assert.Equal(ReleaseUpdateStatus.Failed, (await service.CheckAsync("1.0.0", "x64", false)).Status);
    }

    [Theory]
    [InlineData("https://evil.example/app.zip")]
    [InlineData("https://github.com/moooyo/WinVaultWarden/releases/download/v1.1.0/WinVaultWarden-1.2.0-x64-portable.zip")]
    [InlineData("https://github.com/other/repo/releases/download/v1.2.0/WinVaultWarden-1.2.0-x64-portable.zip")]
    public async Task UntrustedAsset_IsNotOffered(string url)
    {
        using var service = Service(Json(assetUrl: url));
        var result = await service.CheckAsync("1.0.0", "x64", false);
        Assert.Equal(ReleaseUpdateStatus.Unavailable, result.Status);
        Assert.Null(result.Asset);
        Assert.NotNull(result.ReleasePage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DraftOrPrereleaseMetadata_IsRejected(bool draft, bool prerelease)
    {
        using var service = Service(Json(draft: draft, prerelease: prerelease));
        Assert.Equal(ReleaseUpdateStatus.Failed, (await service.CheckAsync("1.0.0", "x64", false)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnavailableService_NeverClaimsUpToDate(HttpStatusCode status)
    {
        using var service = new ReleaseUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        Assert.Equal(ReleaseUpdateStatus.Unavailable, (await service.CheckAsync("1.0.0", "x64", false)).Status);
    }

    [Fact]
    public async Task RedirectResponse_IsRejectedWithoutFollowingLocation()
    {
        var calls = 0;
        using var service = new ReleaseUpdateService(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://evil.example/");
            return Task.FromResult(response);
        }));
        Assert.Equal(ReleaseUpdateStatus.Failed, (await service.CheckAsync("1.0.0", "x64", false)).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OversizedUnadvertisedResponse_IsRejected()
    {
        using var service = new ReleaseUpdateService(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string(' ', 512 * 1024 + 1)))),
        })));
        Assert.Equal(ReleaseUpdateStatus.Failed, (await service.CheckAsync("1.0.0", "x64", false)).Status);
    }

    [Fact]
    public async Task Timeout_IsUnavailableAndCallerCancellationPropagates()
    {
        using var service = new ReleaseUpdateService(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Response(Json());
        }), TimeSpan.FromMilliseconds(20));
        Assert.Equal(ReleaseUpdateStatus.Unavailable, (await service.CheckAsync("1.0.0", "x64", false)).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync("1.0.0", "x64", false, cancelled.Token));
    }

    [Fact]
    public async Task Request_IsAnonymousAndUsesFixedOfficialEndpoint()
    {
        using var service = new ReleaseUpdateService(new Handler((request, _) =>
        {
            Assert.Equal("https://api.github.com/repos/moooyo/WinVaultWarden/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
            return Task.FromResult(Response(Json()));
        }));
        Assert.Equal(ReleaseUpdateStatus.UpdateAvailable, (await service.CheckAsync("1.0.0", "x64", false)).Status);
    }

    private static ReleaseUpdateService Service(string json) => new(new Handler((_, _) => Task.FromResult(Response(json))));
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Json(string? page = null, string? assetUrl = null, bool draft = false, bool prerelease = false) =>
        JsonSerializer.Serialize(new
        {
            tag_name = "v1.2.0",
            html_url = page ?? "https://github.com/moooyo/WinVaultWarden/releases/tag/v1.2.0",
            draft,
            prerelease,
            assets = new[] { "x64", "arm64" }.SelectMany(arch => new[] { "-portable.zip", ".msix" }.Select(suffix =>
            {
                var name = $"WinVaultWarden-1.2.0-{arch}{suffix}";
                return new
                {
                    name,
                    state = "uploaded",
                    size = 123456,
                    digest = $"sha256:{new string('a', 64)}",
                    browser_download_url = assetUrl ?? $"https://github.com/moooyo/WinVaultWarden/releases/download/v1.2.0/{name}",
                };
            })),
        });

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
