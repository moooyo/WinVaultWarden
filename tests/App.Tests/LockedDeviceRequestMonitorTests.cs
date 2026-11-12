using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Api.Dtos;
using App.Services;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class LockedDeviceRequestMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly ServerEndpoints Endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/subpath");
    private static readonly Lazy<string> RequestPublicKey = new(() => PublicKey(2048));
    private const string AccountId = "recipient-account";
    private const string Email = "recipient@example.com";
    private const string RequestId = "2de67c0b-d7b8-48cf-b925-886cf877f94a";

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing-token")]
    [InlineData("wrong-subject")]
    [InlineData("expired-token")]
    [InlineData("missing-expiry")]
    [InlineData("text-expiry")]
    [InlineData("invalid-token")]
    [InlineData("locked")]
    public async Task Arm_RequiresExplicitConsentAndCurrentOnlineAccountToken(string reason)
    {
        using var context = new Context();
        switch (reason)
        {
            case "missing-token":
                context.Session.SetTokens(string.Empty, "refresh-is-not-for-this-monitor");
                break;
            case "wrong-subject":
                context.Session.SetTokens(Jwt("other-account", Now.AddHours(1)), "refresh");
                break;
            case "expired-token":
                context.Session.SetTokens(Jwt(AccountId, Now), "refresh");
                break;
            case "missing-expiry":
                context.Session.SetTokens(Token(new { sub = AccountId }), "refresh");
                break;
            case "text-expiry":
                context.Session.SetTokens(Token(new { sub = AccountId, exp = Now.AddHours(1).ToUnixTimeSeconds().ToString() }), "refresh");
                break;
            case "invalid-token":
                context.Session.SetTokens("malformed", "refresh");
                break;
            case "locked":
                context.Session.Lock();
                break;
        }

        Assert.False(context.Monitor.ArmFromUnlocked(context.Session.CaptureLease(), reason != "disabled"));
        await context.Monitor.RefreshAsync();
        Assert.False(context.Monitor.IsActive);
        Assert.Empty(context.Monitor.Notices);
        Assert.Empty(context.Calls);
    }

    [Fact]
    public async Task OptIn_KeepsOnlyReadOnlyMonitoringAfterOrdinaryLockErasesVaultCredentials()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        Assert.Null(context.Session.AccessToken);
        Assert.Null(context.Session.RefreshToken);
        Assert.Null(context.Session.UserKey);

        await context.Monitor.RefreshAsync();

        Assert.True(context.Monitor.IsActive);
        var notice = Assert.Single(context.Monitor.Notices);
        Assert.Equal(AccountId, notice.Account.AccountId);
        Assert.Equal(Endpoints, notice.Account.Endpoints);
        Assert.Equal(Email, notice.Account.Email);
        Assert.Equal(RequestId, notice.Request.Id);
        Assert.Equal(RequestPublicKey.Value, notice.Request.PublicKey);
        Assert.NotEmpty(notice.Request.FingerprintPhrase);
        Assert.Equal(context.RequestCreation.AddMinutes(15), notice.Request.ExpiresAt);
        var call = Assert.Single(context.Calls);
        Assert.Equal("/subpath/api/auth-requests/pending", call.Path);
        Assert.Equal(context.InitialToken, call.Bearer);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task WhileUnlockedOrPresentationBlocked_MetadataCannotAppearOrRequestUnlock()
    {
        using var context = new Context();
        Assert.True(context.Monitor.ArmFromUnlocked(context.Session.CaptureLease(), true));
        await context.Monitor.RefreshAsync();
        Assert.Empty(context.Monitor.Notices);
        context.Session.Lock();
        context.CanPresent = false;
        await context.Monitor.RefreshAsync();
        Assert.True(context.Monitor.IsActive);
        Assert.Empty(context.Monitor.Notices);
        context.CanPresent = true;
        await context.Monitor.RefreshAsync();
        var notice = Assert.Single(context.Monitor.Notices);
        context.CanPresent = false;
        Assert.False(context.Monitor.RequestUnlock(notice));
        AssertReadOnly(context);
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("network")]
    [InlineData("expiry")]
    [InlineData("stop")]
    [InlineData("logout")]
    [InlineData("account-switch")]
    [InlineData("endpoint-switch")]
    public async Task InvalidCredentialScope_StopsAndDropsConfirmedMetadata(string reason)
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        var notice = Assert.Single(context.Monitor.Notices);
        switch (reason)
        {
            case "unauthorized":
                context.Reply = (_, _) => Task.FromResult(JsonResponse(new { error = "unauthorized" }, HttpStatusCode.Unauthorized));
                break;
            case "network":
                context.Reply = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic failure"));
                break;
            case "expiry":
                context.Clock.UtcNow = Now.AddHours(1);
                break;
            case "stop":
                context.Monitor.Stop();
                break;
            case "logout":
                context.Session.Logout();
                break;
            case "account-switch":
                context.BeginOnlineSession("other-account", Endpoints);
                break;
            case "endpoint-switch":
                context.BeginOnlineSession(AccountId, ServerEndpoints.ForSelfHosted("https://other.example"));
                break;
        }
        await context.Monitor.RefreshAsync();

        Assert.False(context.Monitor.IsActive);
        Assert.Empty(context.Monitor.Notices);
        Assert.False(context.Monitor.RequestUnlock(notice));
        var count = context.Calls.Count;
        await context.Monitor.RefreshAsync();
        Assert.Equal(count, context.Calls.Count);
        AssertReadOnly(context);
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("denied")]
    [InlineData("response-date")]
    [InlineData("encrypted-key")]
    [InlineData("password-hash")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("missing-date")]
    [InlineData("missing-id")]
    [InlineData("missing-device")]
    [InlineData("oversized-ip")]
    [InlineData("bad-base64")]
    [InlineData("small-rsa")]
    [InlineData("trailing-rsa-data")]
    public async Task UnconfirmedOrMalformedRequests_AreNeverPresented(string reason)
    {
        using var context = new Context();
        var request = context.ValidRequest();
        request = reason switch
        {
            "approved" => request with { RequestApproved = true },
            "denied" => request with { RequestApproved = false },
            "response-date" => request with { ResponseDate = Now.ToString("O") },
            "encrypted-key" => request with { Key = "unexpected-key" },
            "password-hash" => request with { MasterPasswordHash = "unexpected-hash" },
            "expired" => request with { CreationDate = Now.AddMinutes(-15).ToString("O") },
            "future" => request with { CreationDate = Now.AddMinutes(2).ToString("O") },
            "missing-date" => request with { CreationDate = "invalid-date" },
            "missing-id" => request with { Id = string.Empty },
            "missing-device" => request with { RequestDeviceType = string.Empty },
            "oversized-ip" => request with { RequestIpAddress = new string('1', 257) },
            "bad-base64" => request with { PublicKey = "bad-base64" },
            "small-rsa" => request with { PublicKey = PublicKey(1024) },
            "trailing-rsa-data" => request with { PublicKey = Convert.ToBase64String([.. Convert.FromBase64String(request.PublicKey), 0]) },
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
        context.Reply = (_, _) => Task.FromResult(ListResponse(request));
        Assert.True(context.ArmAndLock());

        await context.Monitor.RefreshAsync();

        Assert.True(context.Monitor.IsActive);
        Assert.Empty(context.Monitor.Notices);
        AssertReadOnly(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousOrOversizedList_StopsTheScope(bool oversized)
    {
        using var context = new Context();
        var rows = oversized
            ? Enumerable.Range(0, 257).Select(index => context.ValidRequest() with { Id = index.ToString() }).ToArray()
            : new[] { context.ValidRequest(), context.ValidRequest() };
        context.Reply = (_, _) => Task.FromResult(ListResponse(rows));
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        Assert.False(context.Monitor.IsActive);
        Assert.Empty(context.Monitor.Notices);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task UnlockIntent_RequiresTheExactConfirmedNotice_AndNeverApprovesWhileLocked()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        var notice = Assert.Single(context.Monitor.Notices);
        var unlocks = new List<LockedDeviceRequestAccount>();
        context.Monitor.UnlockRequested += unlocks.Add;

        Assert.False(context.Monitor.RequestUnlock(notice with { }));
        Assert.False(context.Monitor.RequestUnlock(notice with { Epoch = Guid.NewGuid() }));
        Assert.True(context.Monitor.RequestUnlock(notice));
        Assert.Single(unlocks);
        Assert.Equal(notice.Account, unlocks[0]);
        var result = await context.Monitor.ResumeAfterUnlockAsync(context.Session.CaptureLease());
        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Single(context.Calls);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task UnchangedPollingResult_PreservesTheNoticeIdentityForAnOpenMetadataWindow()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        var reviewed = Assert.Single(context.Monitor.Notices);

        await context.Monitor.RefreshAsync();

        Assert.Same(reviewed, Assert.Single(context.Monitor.Notices));
        Assert.True(context.Monitor.RequestUnlock(reviewed));
        Assert.Empty(context.Monitor.Notices);
        await context.Monitor.RefreshAsync();
        Assert.Empty(context.Monitor.Notices);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task ChangedPollingResult_ReplacesTheNoticeAndRevokesTheOldWindowAction()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        var old = Assert.Single(context.Monitor.Notices);
        context.Reply = (_, _) => Task.FromResult(ListResponse(context.ValidRequest() with { RequestIpAddress = "203.0.113.5" }));

        await context.Monitor.RefreshAsync();

        var current = Assert.Single(context.Monitor.Notices);
        Assert.NotSame(old, current);
        Assert.False(context.Monitor.RequestUnlock(old));
        Assert.True(context.Monitor.RequestUnlock(current));
        AssertReadOnly(context);
    }

    [Fact]
    public async Task RealSameAccountUnlock_RevalidatesListAndSingleItem_WithFreshAuthorization()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        var notice = Assert.Single(context.Monitor.Notices);
        Assert.True(context.Monitor.RequestUnlock(notice));
        var lease = context.BeginOnlineSession(AccountId, Endpoints, "fresh-online-unlock");
        var freshToken = context.Session.AccessToken;

        var result = await context.Monitor.ResumeAfterUnlockAsync(lease);

        Assert.True(result.IsSuccess);
        Assert.Equal(notice.Request.PublicKey, result.Value!.PublicKey);
        Assert.Equal(notice.Request.FingerprintPhrase, result.Value.FingerprintPhrase);
        Assert.Equal(3, context.Calls.Count);
        Assert.Equal("/subpath/api/auth-requests/pending", context.Calls[1].Path);
        Assert.Equal($"/subpath/api/auth-requests/{RequestId}", context.Calls[2].Path);
        Assert.All(context.Calls.Skip(1), call => Assert.Equal(freshToken, call.Bearer));
        Assert.NotEqual(context.InitialToken, freshToken);
        var consumed = await context.Monitor.ResumeAfterUnlockAsync(lease);
        Assert.Equal(UiOperationStatus.Cancelled, consumed.Status);
        Assert.Equal(3, context.Calls.Count);
        AssertReadOnly(context);
    }

    [Theory]
    [InlineData("public-key")]
    [InlineData("created")]
    [InlineData("ip")]
    [InlineData("device")]
    [InlineData("approved")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task ChangedRequestAfterUnlock_CannotResumeAnOldReview(string reason)
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        var notice = Assert.Single(context.Monitor.Notices);
        Assert.True(context.Monitor.RequestUnlock(notice));
        var lease = context.BeginOnlineSession();
        var changed = reason switch
        {
            "public-key" => context.ValidRequest() with { PublicKey = PublicKey(2048) },
            "created" => context.ValidRequest() with { CreationDate = Now.AddMinutes(-2).ToString("O") },
            "ip" => context.ValidRequest() with { RequestIpAddress = "203.0.113.2" },
            "device" => context.ValidRequest() with { RequestDeviceType = "Different device" },
            "approved" => context.ValidRequest() with { RequestApproved = true },
            _ => context.ValidRequest(),
        };
        context.Reply = (call, _) => Task.FromResult(call.Path.EndsWith("/pending", StringComparison.Ordinal)
            ? reason switch
            {
                "missing" => ListResponse(),
                "duplicate" => ListResponse(changed, changed),
                _ => ListResponse(changed),
            }
            : JsonResponse(changed));

        var result = await context.Monitor.ResumeAfterUnlockAsync(lease);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Null(result.Value);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task SingleItemMustStillMatch_AfterThePendingListHasBeenVerified()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        Assert.True(context.Monitor.RequestUnlock(Assert.Single(context.Monitor.Notices)));
        var lease = context.BeginOnlineSession();
        context.Reply = (call, _) => Task.FromResult(call.Path.EndsWith("/pending", StringComparison.Ordinal)
            ? ListResponse(context.ValidRequest())
            : JsonResponse(context.ValidRequest() with { PublicKey = PublicKey(2048) }));

        var result = await context.Monitor.ResumeAfterUnlockAsync(lease);
        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(3, context.Calls.Count);
        AssertReadOnly(context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentUnlockAccountOrEnvironment_CannotResumeTheIntent(bool changeEnvironment)
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        Assert.True(context.Monitor.RequestUnlock(Assert.Single(context.Monitor.Notices)));
        var lease = context.BeginOnlineSession(changeEnvironment ? AccountId : "other-account",
            changeEnvironment ? ServerEndpoints.ForSelfHosted("https://other.example") : Endpoints);

        var result = await context.Monitor.ResumeAfterUnlockAsync(lease);
        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Single(context.Calls);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task SensitiveCleanup_DropsPresentationAndUnlockIntent_ButPreservesOptedInCredential()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        Assert.True(context.Monitor.RequestUnlock(Assert.Single(context.Monitor.Notices)));
        context.Monitor.ClearSensitive();
        Assert.Empty(context.Monitor.Notices);
        Assert.True(context.Monitor.IsActive);
        var lease = context.BeginOnlineSession();
        Assert.Equal(UiOperationStatus.Cancelled, (await context.Monitor.ResumeAfterUnlockAsync(lease)).Status);
        Assert.Single(context.Calls);
        context.Session.Lock();
        await context.Monitor.RefreshAsync();
        Assert.Single(context.Monitor.Notices);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task DelayedResponse_CannotRepopulatePresentationAfterSensitiveCleanup()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Reply = (_, _) => { entered.TrySetResult(true); return response.Task; };
        var refresh = context.Monitor.RefreshAsync();
        await entered.Task;
        context.Monitor.ClearSensitive();
        response.SetResult(ListResponse(context.ValidRequest()));
        await refresh;

        Assert.True(context.Monitor.IsActive);
        Assert.Empty(context.Monitor.Notices);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task OldEpochResponse_CannotReplaceNewAccountScopeMetadata()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Reply = (_, _) => { entered.TrySetResult(true); return oldResponse.Task; };
        var oldRefresh = context.Monitor.RefreshAsync();
        await entered.Task;
        var freshLease = context.BeginOnlineSession("new-account", Endpoints);
        Assert.True(context.Monitor.ArmFromUnlocked(freshLease, true));
        context.Session.Lock();
        context.Reply = (_, _) => Task.FromResult(ListResponse(context.ValidRequest() with { Id = "new-request" }));
        await context.Monitor.RefreshAsync();
        oldResponse.SetResult(ListResponse(context.ValidRequest()));
        await oldRefresh;

        Assert.True(context.Monitor.IsActive);
        var notice = Assert.Single(context.Monitor.Notices);
        Assert.Equal("new-account", notice.Account.AccountId);
        Assert.Equal("new-request", notice.Request.Id);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task StaleArmCallback_CannotStopARecentlyArmedAccount()
    {
        using var context = new Context();
        var staleLease = context.Session.CaptureLease();
        var currentLease = context.BeginOnlineSession("new-account", Endpoints);
        Assert.True(context.Monitor.ArmFromUnlocked(currentLease, true));

        Assert.False(context.Monitor.ArmFromUnlocked(staleLease, true));

        context.Session.Lock();
        await context.Monitor.RefreshAsync();
        Assert.True(context.Monitor.IsActive);
        Assert.Equal("new-account", Assert.Single(context.Monitor.Notices).Account.AccountId);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task SensitiveCleanupDuringResume_RevokesTheInFlightUnlockIntent()
    {
        using var context = new Context();
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        Assert.True(context.Monitor.RequestUnlock(Assert.Single(context.Monitor.Notices)));
        var lease = context.BeginOnlineSession();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Reply = (call, _) =>
        {
            if (call.Path.EndsWith("/pending", StringComparison.Ordinal))
                return Task.FromResult(ListResponse(context.ValidRequest()));
            entered.TrySetResult(true);
            return response.Task;
        };
        var resuming = context.Monitor.ResumeAfterUnlockAsync(lease);
        await entered.Task;
        context.Monitor.ClearSensitive();
        response.SetResult(JsonResponse(context.ValidRequest()));

        var result = await resuming;
        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Null(result.Value);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task CredentialExpiryDuringResume_RevokesTheInFlightUnlockIntent()
    {
        using var context = new Context();
        context.Session.SetTokens(Jwt(AccountId, Now.AddMinutes(5)), "refresh");
        Assert.True(context.ArmAndLock());
        await context.Monitor.RefreshAsync();
        Assert.True(context.Monitor.RequestUnlock(Assert.Single(context.Monitor.Notices)));
        var lease = context.BeginOnlineSession();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Reply = (call, _) =>
        {
            if (call.Path.EndsWith("/pending", StringComparison.Ordinal))
                return Task.FromResult(ListResponse(context.ValidRequest()));
            entered.TrySetResult(true);
            return response.Task;
        };
        var resuming = context.Monitor.ResumeAfterUnlockAsync(lease);
        await entered.Task;
        context.Clock.UtcNow = Now.AddMinutes(6);
        response.SetResult(JsonResponse(context.ValidRequest()));

        var result = await resuming;
        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.Null(result.Value);
        AssertReadOnly(context);
    }

    [Fact]
    public async Task LongLivedOnlineToken_IsCappedAtTwoHoursForLockedMonitoring()
    {
        using var context = new Context();
        context.Session.SetTokens(Jwt(AccountId, Now.AddDays(1)), "refresh");
        Assert.True(context.ArmAndLock());
        context.Clock.UtcNow = Now.AddHours(2);

        await context.Monitor.RefreshAsync();

        Assert.False(context.Monitor.IsActive);
        Assert.Empty(context.Monitor.Notices);
        Assert.Empty(context.Calls);
    }

    private static void AssertReadOnly(Context context)
    {
        Assert.All(context.Calls, call =>
        {
            Assert.Equal(HttpMethod.Get, call.Method);
            Assert.StartsWith("/subpath/api/auth-requests", call.Path);
        });
        Assert.Equal(0, context.UnlockedRefreshCalls);
    }

    private static string PublicKey(int bits)
    {
        using var rsa = RSA.Create(bits);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    private static string Jwt(string subject, DateTimeOffset expiry, string purpose = "initial") =>
        Token(new { sub = subject, exp = expiry.ToUnixTimeSeconds(), purpose });

    private static string Token(object payload)
    {
        static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode("{\"alg\":\"none\"}")}.{Encode(JsonSerializer.Serialize(payload))}.test-signature";
    }

    private static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ListResponse(params AuthRequestResponse[] requests) =>
        JsonResponse(new AuthRequestListResponse(requests.ToList()));

    private sealed record Call(HttpMethod Method, string Path, string? Bearer);

    private sealed class Context : IDisposable
    {
        public readonly VaultSession Session = new();
        public readonly Clock Clock = new();
        public readonly List<Call> Calls = [];
        public readonly VaultApiClientProvider UnlockedClients;
        public readonly LockedDeviceRequestMonitor Monitor;
        public bool CanPresent = true;
        public string InitialToken { get; }
        public DateTimeOffset RequestCreation => Now.AddMinutes(-1);
        public Func<Call, CancellationToken, Task<HttpResponseMessage>> Reply;
        public int UnlockedRefreshCalls;

        public Context()
        {
            Reply = (call, _) => Task.FromResult(call.Path.EndsWith("/pending", StringComparison.Ordinal)
                ? ListResponse(ValidRequest()) : JsonResponse(ValidRequest()));
            var factory = new ApiClientFactory(_ => new Handler(this));
            UnlockedClients = new VaultApiClientProvider(factory, Session, (_, _, _) =>
            {
                UnlockedRefreshCalls++;
                return Task.FromResult(false);
            });
            Monitor = new(Session, factory, UnlockedClients, new FingerprintService(), () => CanPresent, Clock);
            BeginOnlineSession();
            InitialToken = Session.AccessToken!;
        }

        public VaultSessionLease BeginOnlineSession(string account = AccountId, ServerEndpoints? endpoints = null, string purpose = "initial")
        {
            var lease = Session.BeginAccountSession(account, Email, endpoints ?? Endpoints);
            Session.SetTokens(Jwt(account, Now.AddHours(1), purpose), "refresh-must-not-leave-vault-session");
            Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
            return lease;
        }

        public bool ArmAndLock()
        {
            var armed = Monitor.ArmFromUnlocked(Session.CaptureLease(), true);
            Session.Lock();
            return armed;
        }

        public AuthRequestResponse ValidRequest() => new(
            RequestId, RequestPublicKey.Value, "Windows", "203.0.113.1", null, null,
            RequestCreation.ToString("O"), null, null, Endpoints.WebVault.Value);

        public void Dispose() { Monitor.Dispose(); UnlockedClients.Dispose(); Session.Lock(); }
    }

    private sealed class Handler(Context context) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = new Call(request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter);
            context.Calls.Add(call);
            return context.Reply(call, cancellationToken);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
