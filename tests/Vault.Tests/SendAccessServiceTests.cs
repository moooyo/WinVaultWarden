using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class SendAccessServiceTests
{
    private static (SendAccessService service, FakeSendApiClient api, SendCryptoService crypto) NewService(
        TimeProvider? timeProvider = null)
    {
        var crypto = new SendCryptoService(new CryptoService());
        var api = new FakeSendApiClient();
        return (
            new SendAccessService(
                FakeVaultApiClientProvider.ForAnonymous<IPublicSendApiClient>(api),
                crypto,
                new TransferIdleTimeoutPolicy(Timeout.InfiniteTimeSpan),
                timeProvider),
            api,
            crypto);
    }

    [Fact]
    public async Task Access_TextSend_ParsesUrlDerivesKeyAndDecrypts()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-1", seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-1",
            Type: 0,
            Name: crypto.EncryptField("Shared Note", cryptoKey),
            Text: new SendTextDto(Text: crypto.EncryptField("hello world", cryptoKey), Hidden: false),
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        var result = await service.AccessAsync(url, password: null, TestContext.Current.CancellationToken);

        Assert.Equal("acc-1", api.LastAccessId);
        Assert.Null(api.LastAccessPasswordProof);
        Assert.Equal(SendType.Text, result.Type);
        Assert.Equal("Shared Note", result.Name);
        Assert.Equal("hello world", result.TextContent);
        Assert.Equal("acc-1", result.AccessId);
        Assert.Equal(seed, result.Seed);
    }

    [Fact]
    public async Task Access_WithPassword_SendsProofNotPlaintext()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-2", seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-2",
            Type: 0,
            Name: crypto.EncryptField("n", cryptoKey),
            Text: new SendTextDto(Text: crypto.EncryptField("t", cryptoKey), Hidden: false),
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        await service.AccessAsync(url, password: "letmein", TestContext.Current.CancellationToken);

        var proof = api.LastAccessPasswordProof;
        Assert.False(string.IsNullOrEmpty(proof));
        Assert.NotEqual("letmein", proof);
        Assert.Equal(32, Convert.FromBase64String(proof!).Length);
    }

    [Fact]
    public async Task Access_FileSend_PopulatesFileNameAndDownloadInfo()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-3", seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-3",
            Type: 1,
            Name: crypto.EncryptField("File Send", cryptoKey),
            Text: null,
            File: new SendFileDto(Id: "f-3", FileName: crypto.EncryptField("data.bin", cryptoKey), Size: 999L, SizeName: "999 Bytes"),
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        var result = await service.AccessAsync(url, password: null, TestContext.Current.CancellationToken);

        Assert.Equal(SendType.File, result.Type);
        Assert.Equal("File Send", result.Name);
        Assert.Equal("data.bin", result.FileName);
        // 下载信息由 SendId+FileId 提供(真实服务端两步协议);FileDownloadUrl 置空。
        Assert.Equal("s-3", result.SendId);
        Assert.Equal("f-3", result.FileId);
        Assert.Equal(seed, result.Seed);
    }

    [Fact]
    public async Task Access_UnsupportedItemSend_FailsClosed()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-item", seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-item",
            Type: 2,
            Name: crypto.EncryptField("Item Send", cryptoKey),
            Text: null,
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.AccessAsync(
                url,
                password: null,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadFile_DecryptsEncArrayBuffer()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var cryptoKey = crypto.DeriveCryptoKey(seed);
        var plaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        api.DownloadBytes = crypto.EncryptToBuffer(plaintext, cryptoKey);

        var accessed = new SendAccessResult
        {
            Type = SendType.File,
            Name = "f",
            FileName = "data.bin",
            FileDownloadUrl = "https://vault.example/dl/f-3",
            AccessId = "acc-3",
            Endpoints = ServerEndpoints.FromWebVault("https://vault.example"),
            Seed = seed,
        };

        await using var destination = new MemoryStream();
        using var download = accessed.AcquireDownloadLease();
        accessed.Dispose();
        var written = await service.DownloadFileAsync(
            download,
            destination,
            TestContext.Current.CancellationToken);

        Assert.Equal("https://vault.example/dl/f-3", api.LastDownloadUrl);
        Assert.Equal(plaintext.Length, written);
        Assert.Equal(plaintext, destination.ToArray());
    }

    [Fact]
    public async Task DownloadFile_DisposedLeaseMakesNoApiCall()
    {
        var (service, api, _) = NewService();
        using var accessed = new SendAccessResult
        {
            Type = SendType.File,
            SendId = "s-disposed",
            FileId = "f-disposed",
            AccessId = "acc-disposed",
            Endpoints = ServerEndpoints.FromWebVault("https://vault.example"),
            Seed = new byte[16],
        };
        var download = accessed.AcquireDownloadLease();
        download.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.DownloadFileAsync(download, Stream.Null, CancellationToken.None));

        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task DownloadFile_LeaseDisposedAfterCapabilityCopy_DoesNotAbortOwnedDownload()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        var plaintext = new byte[] { 9, 8, 7, 6 };
        api.DownloadBytes = crypto.EncryptToBuffer(plaintext, cryptoKey);
        api.AccessFileResult = new SendFileDownloadResponse(
            "f-owned",
            "https://vault.example/api/sends/s-owned/f-owned?t=token",
            "send-fileDownload");
        var accessStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accessGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.AfterAccessFileStarted = accessStarted.SetResult;
        api.AccessFileGate = accessGate.Task;
        using var accessed = new SendAccessResult
        {
            Type = SendType.File,
            SendId = "s-owned",
            FileId = "f-owned",
            AccessId = "acc-owned",
            Endpoints = ServerEndpoints.FromWebVault("https://vault.example"),
            Seed = seed,
        };
        var download = accessed.AcquireDownloadLease();
        await using var destination = new MemoryStream();

        var operation = service.DownloadFileAsync(
            download,
            destination,
            TestContext.Current.CancellationToken);
        await accessStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        download.Dispose();
        accessGate.SetResult();

        Assert.Equal(plaintext.LongLength, await operation);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Equal(new[] { "access-file", "download" }, api.Calls);
    }

    [Fact]
    public async Task DownloadFile_LeaseDisposedAfterCapabilityCopy_StillHonorsCallerCancellation()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var accessStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var accessGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.AfterAccessFileStarted = accessStarted.SetResult;
        api.AccessFileGate = accessGate.Task;
        using var accessed = new SendAccessResult
        {
            Type = SendType.File,
            SendId = "s-cancel",
            FileId = "f-cancel",
            AccessId = "acc-cancel",
            Endpoints = ServerEndpoints.FromWebVault("https://vault.example"),
            Seed = seed,
        };
        var download = accessed.AcquireDownloadLease();
        using var cancellation = new CancellationTokenSource();

        var operation = service.DownloadFileAsync(download, Stream.Null, cancellation.Token);
        await accessStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        download.Dispose();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(new[] { "access-file" }, api.Calls);
    }

    [Fact]
    public async Task Access_WrongPassword_PropagatesServerError()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var url = crypto.BuildShareUrl("https://vault.example", "acc-4", seed);
        api.AccessException = new HttpRequestException("401 Unauthorized");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.AccessAsync(url, password: "wrong", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Access_OfficialServer_MintsAndSpendsScopedToken()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        var url = crypto.BuildShareUrl("https://vault.example", "acc-modern", seed);
        api.AccessTokenResult = new SendAccessTokenResult.Success("send-token", 300);
        api.AccessResult = new SendAccessResponseDto(
            Id: "acc-modern",
            Type: 0,
            Name: crypto.EncryptField("Modern Send", cryptoKey),
            Text: new SendTextDto(crypto.EncryptField("modern text", cryptoKey), false),
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        using var result = await service.AccessAsync(
            url,
            password: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.UsesAccessToken);
        Assert.Equal("modern text", result.TextContent);
        Assert.Equal("send-token", api.LastAccessToken);
        Assert.Equal(["request-access-token", "access-with-token"], api.Calls);
    }

    [Theory]
    [InlineData("password_hash_b64_required")]
    [InlineData("password_hash_b64_invalid")]
    public async Task Access_OfficialPasswordError_IsUnauthorized(string errorType)
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var url = crypto.BuildShareUrl("https://vault.example", "acc-modern", seed);
        api.AccessTokenResult = new SendAccessTokenResult.Error(
            400,
            "invalid_request",
            errorType,
            errorType);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AccessAsync(url, "wrong", TestContext.Current.CancellationToken));

        Assert.Equal(["request-access-token"], api.Calls);
    }

    [Fact]
    public async Task Access_OfficialEmailAuthentication_FailsClosed()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        var url = crypto.BuildShareUrl("https://vault.example", "acc-modern", seed);
        api.AccessTokenResult = new SendAccessTokenResult.Error(
            400,
            "invalid_request",
            "email is required",
            "email_required");

        await Assert.ThrowsAsync<SendUnsupportedAuthenticationException>(() =>
            service.AccessAsync(url, null, TestContext.Current.CancellationToken));

        Assert.Equal(["request-access-token"], api.Calls);
    }

    [Fact]
    public async Task DownloadFile_ModernSendAtSingleAccessLimit_ReusesUnexpiredInitialToken()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));
        var (service, api, crypto) = NewService(clock);
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        var plaintext = new byte[] { 5, 4, 3, 2, 1 };
        api.AccessTokenAction = (requestNumber, _) => Task.FromResult<SendAccessTokenResult>(
            requestNumber == 1
                ? new SendAccessTokenResult.Success("single-use-token", 1)
                : new SendAccessTokenResult.Error(
                    400,
                    "invalid_grant",
                    "Maximum access count reached.",
                    null));
        api.AccessResult = new SendAccessResponseDto(
            Id: "acc-modern",
            Type: 1,
            Name: crypto.EncryptField("Modern file", cryptoKey),
            Text: null,
            File: new SendFileDto(
                "file-modern",
                crypto.EncryptField("modern.bin", cryptoKey),
                plaintext.LongLength,
                "5 Bytes"),
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");
        api.AccessFileResult = new SendFileDownloadResponse(
            "file-modern",
            "https://vault.example/api/sends/download-modern",
            "send-fileDownload");
        api.DownloadBytes = crypto.EncryptToBuffer(plaintext, cryptoKey);
        var shareUrl = crypto.BuildShareUrl("https://vault.example", "acc-modern", seed);
        var accessed = await service.AccessAsync(
            shareUrl,
            password: null,
            TestContext.Current.CancellationToken);
        using var download = accessed.AcquireDownloadLease();
        accessed.Dispose();
        await using var destination = new MemoryStream();

        var written = await service.DownloadFileAsync(
            download,
            destination,
            TestContext.Current.CancellationToken);

        Assert.Equal(plaintext.LongLength, written);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Equal(
            ["request-access-token", "access-with-token", "access-file-with-token", "download"],
            api.Calls);
        Assert.Equal(1, api.AccessTokenRequestCount);
        Assert.Equal("single-use-token", api.LastAccessToken);
    }

    [Fact]
    public async Task DownloadFile_ModernTokenSnapshot_SurvivesConcurrentLeaseDisposal()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));
        var (service, api, crypto) = NewService(clock);
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        var plaintext = new byte[] { 8, 6, 4, 2 };
        var fileAccessStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFileAccess = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.AccessTokenResult = new SendAccessTokenResult.Success("snapshot-token", 300);
        api.AccessResult = new SendAccessResponseDto(
            Id: "acc-snapshot",
            Type: 1,
            Name: crypto.EncryptField("Snapshot file", cryptoKey),
            Text: null,
            File: new SendFileDto(
                "file-snapshot",
                crypto.EncryptField("snapshot.bin", cryptoKey),
                plaintext.LongLength,
                "4 Bytes"),
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");
        api.AccessFileResult = new SendFileDownloadResponse(
            "file-snapshot",
            "https://vault.example/api/sends/download-snapshot",
            "send-fileDownload");
        api.DownloadBytes = crypto.EncryptToBuffer(plaintext, cryptoKey);
        api.AfterAccessFileWithTokenStarted = fileAccessStarted.SetResult;
        api.AccessFileWithTokenGate = releaseFileAccess.Task;
        var shareUrl = crypto.BuildShareUrl("https://vault.example", "acc-snapshot", seed);
        using var accessed = await service.AccessAsync(
            shareUrl,
            password: null,
            TestContext.Current.CancellationToken);
        var download = accessed.AcquireDownloadLease();
        await using var destination = new MemoryStream();

        var operation = service.DownloadFileAsync(
            download,
            destination,
            TestContext.Current.CancellationToken);
        await fileAccessStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        download.Dispose();
        releaseFileAccess.SetResult();

        Assert.Equal(plaintext.LongLength, await operation);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Equal(1, api.AccessTokenRequestCount);
        Assert.Throws<ObjectDisposedException>(() => download.CopySeed());
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public async Task DownloadFile_ModernToken_RespectsAdvertisedExpiryBoundary(
        int elapsedSeconds,
        bool refreshExpected)
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));
        var (service, api, crypto) = NewService(clock);
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        var plaintext = new byte[] { 1, 3, 5, 7 };
        api.AccessTokenAction = (requestNumber, _) => Task.FromResult<SendAccessTokenResult>(
            requestNumber switch
            {
                1 => new SendAccessTokenResult.Success("initial-token", 60),
                2 => new SendAccessTokenResult.Success("refreshed-token", 300),
                _ => new SendAccessTokenResult.Error(
                    400,
                    "invalid_grant",
                    "Unexpected extra token request.",
                    null),
            });
        api.AccessResult = new SendAccessResponseDto(
            Id: "acc-expiring",
            Type: 1,
            Name: crypto.EncryptField("Expiring file", cryptoKey),
            Text: null,
            File: new SendFileDto(
                "file-expiring",
                crypto.EncryptField("expiring.bin", cryptoKey),
                plaintext.LongLength,
                "4 Bytes"),
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");
        api.AccessFileResult = new SendFileDownloadResponse(
            "file-expiring",
            "https://vault.example/api/sends/download-expiring",
            "send-fileDownload");
        api.DownloadBytes = crypto.EncryptToBuffer(plaintext, cryptoKey);
        var shareUrl = crypto.BuildShareUrl("https://vault.example", "acc-expiring", seed);
        using var accessed = await service.AccessAsync(
            shareUrl,
            password: null,
            TestContext.Current.CancellationToken);
        using var download = accessed.AcquireDownloadLease();
        clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
        await using var destination = new MemoryStream();

        var written = await service.DownloadFileAsync(
            download,
            destination,
            TestContext.Current.CancellationToken);

        Assert.Equal(plaintext.LongLength, written);
        Assert.Equal(plaintext, destination.ToArray());
        Assert.Equal(refreshExpected ? 2 : 1, api.AccessTokenRequestCount);
        Assert.Equal(refreshExpected ? "refreshed-token" : "initial-token", api.LastAccessToken);
        string[] expectedCalls = refreshExpected
            ? [
                "request-access-token",
                "access-with-token",
                "request-access-token",
                "access-file-with-token",
                "download",
            ]
            : [
                "request-access-token",
                "access-with-token",
                "access-file-with-token",
                "download",
            ];
        Assert.Equal(expectedCalls, api.Calls);
    }

    [Fact]
    public async Task DownloadFile_ConcurrentExpiredToken_RefreshesOnlyOnce()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));
        var (service, api, crypto) = NewService(clock);
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        var plaintext = new byte[] { 2, 4, 6, 8 };
        var refreshStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        api.AccessTokenAction = async (requestNumber, cancellationToken) =>
        {
            if (requestNumber == 1)
                return new SendAccessTokenResult.Success("initial-token", 1);
            if (requestNumber == 2)
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return new SendAccessTokenResult.Success("shared-refresh-token", 300);
            }

            return new SendAccessTokenResult.Error(
                400,
                "invalid_grant",
                "Unexpected concurrent token request.",
                null);
        };
        api.AccessResult = new SendAccessResponseDto(
            Id: "acc-concurrent",
            Type: 1,
            Name: crypto.EncryptField("Concurrent file", cryptoKey),
            Text: null,
            File: new SendFileDto(
                "file-concurrent",
                crypto.EncryptField("concurrent.bin", cryptoKey),
                plaintext.LongLength,
                "4 Bytes"),
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");
        api.AccessFileResult = new SendFileDownloadResponse(
            "file-concurrent",
            "https://vault.example/api/sends/download-concurrent",
            "send-fileDownload");
        api.DownloadBytes = crypto.EncryptToBuffer(plaintext, cryptoKey);
        var shareUrl = crypto.BuildShareUrl("https://vault.example", "acc-concurrent", seed);
        using var accessed = await service.AccessAsync(
            shareUrl,
            password: null,
            TestContext.Current.CancellationToken);
        using var download = accessed.AcquireDownloadLease();
        clock.Advance(TimeSpan.FromSeconds(1));
        await using var firstDestination = new MemoryStream();
        await using var secondDestination = new MemoryStream();

        var first = service.DownloadFileAsync(
            download,
            firstDestination,
            TestContext.Current.CancellationToken);
        await refreshStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = service.DownloadFileAsync(
            download,
            secondDestination,
            TestContext.Current.CancellationToken);
        releaseRefresh.SetResult();

        var written = await Task.WhenAll(first, second);
        Assert.All(written, count => Assert.Equal(plaintext.LongLength, count));
        Assert.Equal(plaintext, firstDestination.ToArray());
        Assert.Equal(plaintext, secondDestination.ToArray());
        Assert.Equal(2, api.AccessTokenRequestCount);
        Assert.Equal(2, api.Calls.Count(call => call == "access-file-with-token"));
        Assert.Equal(2, api.Calls.Count(call => call == "download"));
    }

    [Fact]
    public async Task Access_MalformedUrl_Throws()
    {
        var (service, _, _) = NewService();

        await Assert.ThrowsAsync<FormatException>(() =>
            service.AccessAsync("https://vault.example/not-a-send-url", password: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Access_SelfHostedSubpath_PreservesWebVaultAndApiPaths()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-5",
            Type: 0,
            Name: crypto.EncryptField("n", cryptoKey),
            Text: new SendTextDto(Text: crypto.EncryptField("t", cryptoKey), Hidden: false),
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        var result = await service.AccessAsync(
            crypto.BuildShareUrl("https://vault.example/warden", "acc-5", seed),
            password: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("https://vault.example/warden", result.Endpoints.WebVault.Value);
        Assert.Equal("https://vault.example/warden/api", result.Endpoints.Api.Value);
        Assert.Equal(new ServerOrigin("https://vault.example"), result.Origin);
    }

    [Fact]
    public async Task Access_OfficialSendHost_UsesBitwardenCloudApi()
    {
        var (service, api, crypto) = NewService();
        var seed = crypto.GenerateSeed();
        using var cryptoKey = crypto.DeriveCryptoKey(seed);
        api.AccessResult = new SendAccessResponseDto(
            Id: "s-cloud",
            Type: 0,
            Name: crypto.EncryptField("n", cryptoKey),
            Text: new SendTextDto(Text: crypto.EncryptField("t", cryptoKey), Hidden: false),
            File: null,
            ExpirationDate: null,
            CreatorIdentifier: null,
            Object: "send-access");

        var result = await service.AccessAsync(
            crypto.BuildShareUrl("https://send.bitwarden.com", "acc-cloud", seed),
            password: null,
            TestContext.Current.CancellationToken);

        Assert.Same(ServerEndpoints.BitwardenUs, result.Endpoints);
        Assert.Equal("https://api.bitwarden.com", result.Endpoints.Api.Value);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
