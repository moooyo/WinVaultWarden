using App.Services;
using App.ViewModels.Models;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class DeviceApprovalDecisionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
    private const string Phrase = "alpha-bravo-charlie-delta-echo";

    [Theory]
    [InlineData(false, Phrase)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public async Task UnreviewedOrBlankFingerprint_DoesNotUnlockOrCallServer(
        bool fingerprintReviewed,
        string phrase)
    {
        using var context = new TestContext(unlocked: false);
        var reviewed = Request() with { FingerprintPhrase = phrase };

        var result = await context.Service.ApproveAsync(reviewed, fingerprintReviewed, UnexpectedUnlock);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        AssertNoServerCalls(context.Requests);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    public async Task RequestAtOrBeyondExpiry_DoesNotUnlockOrCallServer(int ageMinutes)
    {
        using var context = new TestContext(unlocked: false);
        var reviewed = Request() with { CreatedAt = Now.AddMinutes(-ageMinutes) };

        var result = await context.Service.ApproveAsync(reviewed, true, UnexpectedUnlock);

        Assert.False(context.Service.IsPending(reviewed));
        Assert.Equal(UiOperationStatus.Failed, result.Status);
        AssertNoServerCalls(context.Requests);
    }

    [Fact]
    public async Task MissingServerCreationTime_DoesNotUnlockOrCallServer()
    {
        using var context = new TestContext(unlocked: false);
        var reviewed = Request() with { CreatedAt = null };

        var result = await context.Service.ApproveAsync(reviewed, true, UnexpectedUnlock);

        Assert.False(context.Service.IsPending(reviewed));
        Assert.Equal(UiOperationStatus.Failed, result.Status);
        AssertNoServerCalls(context.Requests);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("fingerprint")]
    [InlineData("creation")]
    public async Task RefreshedRequestDiffersFromReviewedRequest_DoesNotApprove(string changedField)
    {
        using var context = new TestContext();
        var reviewed = Request();
        context.Requests.Pending = [changedField switch
        {
            "key" => reviewed with { PublicKey = "replacement-public-key" },
            "fingerprint" => reviewed with { FingerprintPhrase = "other-bravo-charlie-delta-echo" },
            "creation" => reviewed with { CreatedAt = reviewed.CreatedAt!.Value.AddSeconds(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        }];

        var result = await context.Service.ApproveAsync(reviewed, true, UnexpectedUnlock);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(1, context.Requests.ListCalls);
        Assert.Empty(context.Requests.Approvals);
    }

    [Fact]
    public async Task ReviewedRequestNoLongerPending_DoesNotApproveAnotherRequest()
    {
        using var context = new TestContext();
        context.Requests.Pending = [Request() with { Id = "different-request" }];

        var result = await context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(1, context.Requests.ListCalls);
        Assert.Empty(context.Requests.Approvals);
    }

    [Fact]
    public async Task RequestExpiresDuringRefresh_DoesNotApprove()
    {
        using var context = new TestContext();
        context.Requests.ListHandler = _ =>
        {
            context.Clock.UtcNow = Now.AddMinutes(15);
            return Task.FromResult<IReadOnlyList<AuthRequestItem>>([Request()]);
        };

        var result = await context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Empty(context.Requests.Approvals);
    }

    [Fact]
    public async Task ExplicitUnlockNotCompleted_DoesNotCallServer()
    {
        using var context = new TestContext(unlocked: false);
        var unlockCalls = 0;

        var result = await context.Service.ApproveAsync(Request(), true, _ =>
        {
            unlockCalls++;
            return Task.FromResult(false);
        });

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(1, unlockCalls);
        AssertNoServerCalls(context.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnlockChangesAccountOrServer_DoesNotCallServer(bool changeAccount)
    {
        using var context = new TestContext(unlocked: false);

        var result = await context.Service.ApproveAsync(Request(), true, _ =>
        {
            context.Session.BeginAccountSession(
                changeAccount ? "account-b" : "account-a",
                "user@example.com",
                new ServerOrigin(changeAccount ? "https://vault.example" : "https://other.example"));
            context.Unlock();
            return Task.FromResult(true);
        });

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        AssertNoServerCalls(context.Requests);
    }

    [Fact]
    public async Task UnlockReportsSuccessWithoutUnlockedKey_DoesNotCallServer()
    {
        using var context = new TestContext(unlocked: false);

        var result = await context.Service.ApproveAsync(Request(), true, _ => Task.FromResult(true));

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        AssertNoServerCalls(context.Requests);
    }

    [Fact]
    public async Task RealUnlockedSessionAndUnchangedRequest_ApprovesReviewedKeyOnce()
    {
        using var context = new TestContext();

        var result = await context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(1, context.Requests.ListCalls);
        Assert.Equal((Request().Id, Request().PublicKey), Assert.Single(context.Requests.Approvals));
        Assert.True(context.Requests.LastApproveToken.CanBeCanceled);
        Assert.False(context.Requests.LastApproveToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SuccessfulUnlockOfSameAccount_RefreshesBeforeApproval()
    {
        using var context = new TestContext(unlocked: false);
        var unlockCalls = 0;

        var result = await context.Service.ApproveAsync(Request(), true, _ =>
        {
            unlockCalls++;
            AssertNoServerCalls(context.Requests);
            context.Unlock();
            return Task.FromResult(true);
        });

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        Assert.Equal(1, unlockCalls);
        Assert.Equal(1, context.Requests.ListCalls);
        Assert.Single(context.Requests.Approvals);
    }

    [Fact]
    public async Task LockDuringRefresh_CancelsEvenWhenServerIgnoresCancellation()
    {
        using var context = new TestContext();
        var refresh = new TaskCompletionSource<IReadOnlyList<AuthRequestItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Requests.ListHandler = _ => refresh.Task;

        var approval = context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);
        Assert.Equal(1, context.Requests.ListCalls);
        context.Session.Lock();
        refresh.SetResult([Request()]);
        var result = await approval;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.True(context.Requests.LastListToken.IsCancellationRequested);
        Assert.Empty(context.Requests.Approvals);
    }

    [Fact]
    public async Task ConcurrentApproval_IsBusyAndOnlyFirstCallCanApprove()
    {
        using var context = new TestContext();
        var refresh = new TaskCompletionSource<IReadOnlyList<AuthRequestItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Requests.ListHandler = _ => refresh.Task;

        var first = context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);
        var second = await context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);
        refresh.SetResult([Request()]);
        var firstResult = await first;

        Assert.Equal(UiOperationStatus.Busy, second.Status);
        Assert.Equal(UiOperationStatus.Succeeded, firstResult.Status);
        Assert.Equal(1, context.Requests.ListCalls);
        Assert.Single(context.Requests.Approvals);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotUnlockOrCallServer()
    {
        using var context = new TestContext(unlocked: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await context.Service.ApproveAsync(Request(), true, UnexpectedUnlock, cancellation.Token);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        AssertNoServerCalls(context.Requests);
    }

    [Fact]
    public async Task CancellationDuringUnlock_DoesNotCallServer()
    {
        using var context = new TestContext(unlocked: false);
        using var cancellation = new CancellationTokenSource();

        var result = await context.Service.ApproveAsync(Request(), true, ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<bool>(ct);
        }, cancellation.Token);

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        AssertNoServerCalls(context.Requests);
    }

    [Fact]
    public async Task CancellationDuringRefresh_DoesNotApproveAndReleasesOperationGate()
    {
        using var context = new TestContext();
        using var cancellation = new CancellationTokenSource();
        context.Requests.ListHandler = async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [Request()];
        };

        var approval = context.Service.ApproveAsync(Request(), true, UnexpectedUnlock, cancellation.Token);
        Assert.Equal(1, context.Requests.ListCalls);
        cancellation.Cancel();
        var result = await approval;

        Assert.Equal(UiOperationStatus.Cancelled, result.Status);
        Assert.True(context.Requests.LastListToken.IsCancellationRequested);
        Assert.Empty(context.Requests.Approvals);

        context.Requests.ListHandler = null;
        var retry = await context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);

        Assert.Equal(UiOperationStatus.Succeeded, retry.Status);
        Assert.Single(context.Requests.Approvals);
    }

    private static AuthRequestItem Request() => new(
        "request-a", "Windows", "192.0.2.1", "2026/10/10 08:00", "reviewed-public-key")
    {
        FingerprintPhrase = Phrase,
        CreatedAt = Now,
    };

    private static Task<bool> UnexpectedUnlock(CancellationToken _) =>
        throw new Xunit.Sdk.XunitException("Unlock must not be requested for this operation.");

    private static void AssertNoServerCalls(FakeRequests requests)
    {
        Assert.Equal(0, requests.ListCalls);
        Assert.Empty(requests.Approvals);
    }

    [Fact]
    public async Task RefreshTimeout_IsFailureAndNeverApproval()
    {
        using var context = new TestContext();
        context.Requests.ListHandler = _ => Task.FromException<IReadOnlyList<AuthRequestItem>>(
            new TaskCanceledException("request timed out",
                new Api.RequestTimeoutException(Api.RequestTimeoutPhase.ControlPlane, TimeSpan.FromSeconds(30)),
                CancellationToken.None));

        var result = await context.Service.ApproveAsync(Request(), true, UnexpectedUnlock);

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(UiExceptionPolicy.RequestTimeoutMessage, result.Error);
        Assert.Empty(context.Requests.Approvals);
    }

    private sealed class TestContext : IDisposable
    {
        public VaultSession Session { get; } = new();
        public FakeRequests Requests { get; } = new();
        public TestTimeProvider Clock { get; } = new();
        public DeviceApprovalDecisionService Service { get; }

        public TestContext(bool unlocked = true)
        {
            Session.BeginAccountSession("account-a", "user@example.com", new ServerOrigin("https://vault.example"));
            if (unlocked)
                Unlock();
            Service = new DeviceApprovalDecisionService(Requests, Session, Clock);
        }

        public void Unlock() => Session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        public void Dispose() => Session.Lock();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakeRequests : IAuthRequestUiService
    {
        public IReadOnlyList<AuthRequestItem> Pending { get; set; } = [Request()];
        public Func<CancellationToken, Task<IReadOnlyList<AuthRequestItem>>>? ListHandler { get; set; }
        public int ListCalls { get; private set; }
        public List<(string Id, string PublicKey)> Approvals { get; } = [];
        public CancellationToken LastListToken { get; private set; }
        public CancellationToken LastApproveToken { get; private set; }

        public Task<IReadOnlyList<AuthRequestItem>> ListPendingAsync(CancellationToken ct = default)
        {
            ListCalls++;
            LastListToken = ct;
            return ListHandler?.Invoke(ct) ?? Task.FromResult(Pending);
        }

        public Task ApproveAsync(string id, string publicKey, CancellationToken ct = default)
        {
            LastApproveToken = ct;
            Approvals.Add((id, publicKey));
            return Task.CompletedTask;
        }

        public Task DenyAsync(string id, CancellationToken ct = default) =>
            throw new Xunit.Sdk.XunitException("An approval decision must not deny a request.");
    }
}
