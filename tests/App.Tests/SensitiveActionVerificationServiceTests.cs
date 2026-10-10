using App.Services;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class SensitiveActionVerificationServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task EveryActionOrUnknownMode_NeverCachesSuccessfulVerification(int mode)
    {
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier, readMode: () => mode);

        Assert.True(await VerifyAsync(service));
        Assert.True(await VerifyAsync(service));

        Assert.Equal(2, verifier.Calls);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task GraceMode_ReusesUntilExactlyFiveMinutesWithoutExtendingTheWindow()
    {
        var clock = new ManualTimeProvider();
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier, clock: clock);

        Assert.True(await VerifyAsync(service));
        Assert.Equal(TimeSpan.FromMinutes(5), service.GraceRemaining);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(await VerifyAsync(service));
        Assert.Equal(TimeSpan.FromMinutes(1), service.GraceRemaining);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        Assert.True(await VerifyAsync(service));
        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task GraceMode_UsesMonotonicTimeInsteadOfTheSystemClock()
    {
        var clock = new ManualTimeProvider();
        var service = CreateService(SuccessfulVerifier(), clock: clock);
        Assert.True(await VerifyAsync(service));

        clock.UtcNow = clock.UtcNow.AddYears(-10);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(3), service.GraceRemaining);
        clock.UtcNow = clock.UtcNow.AddYears(20);
        Assert.Equal(TimeSpan.FromMinutes(3), service.GraceRemaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRevocationOrSensitiveClear_RequiresFreshVerification(bool clearSensitive)
    {
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier);
        Assert.True(await VerifyAsync(service));

        if (clearSensitive)
            service.ClearSensitive();
        else
            service.EndGracePeriod();

        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        Assert.True(await VerifyAsync(service));
        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task ModeChange_RevokesGraceAndCannotRestoreThePreviousApproval()
    {
        var mode = 1;
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier, readMode: () => mode);
        Assert.True(await VerifyAsync(service));

        mode = 0;
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        Assert.True(await VerifyAsync(service));
        mode = 1;
        Assert.True(await VerifyAsync(service));

        Assert.Equal(3, verifier.Calls);
    }

    [Fact]
    public async Task DeniedVerification_NeverCreatesGrace()
    {
        var verifier = new StubVerifier(_ => Task.FromResult(false));
        var service = CreateService(verifier);

        Assert.False(await VerifyAsync(service));
        Assert.False(await VerifyAsync(service));

        Assert.Equal(2, verifier.Calls);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task PreCancelledRequest_NeverInvokesVerifierOrUsesGrace()
    {
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier);
        Assert.True(await VerifyAsync(service));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(await VerifyAsync(service, cancellation.Token));
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotWaitForOrCacheAnUncooperativeVerifier()
    {
        var completion = NewCompletion();
        var verifier = new StubVerifier(_ => completion.Task);
        var service = CreateService(verifier);
        using var cancellation = new CancellationTokenSource();
        var operation = VerifyAsync(service, cancellation.Token);

        cancellation.Cancel();
        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(verifier.LastToken.IsCancellationRequested);
        completion.SetResult(true);

        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        Assert.True(await VerifyAsync(service));
        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task VerifierCancelsCallerBeforeReturningSuccess_NeverCreatesGrace()
    {
        using var cancellation = new CancellationTokenSource();
        var verifier = new StubVerifier(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        });
        var service = CreateService(verifier);

        Assert.False(await VerifyAsync(service, cancellation.Token));
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task VerifierCancellation_IsReportedAsDeniedWithoutGrace()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var verifier = new StubVerifier(_ => Task.FromCanceled<bool>(cancellation.Token));
        var service = CreateService(verifier);

        Assert.False(await VerifyAsync(service));
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task UnexplainedCancellation_IsNotClassifiedAsUserCancellation()
    {
        var verifier = new StubVerifier(_ => Task.FromException<bool>(new OperationCanceledException()));
        var service = CreateService(verifier);

        await Assert.ThrowsAsync<OperationCanceledException>(() => VerifyAsync(service));
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task VerificationTimeout_DoesNotGrantGrace()
    {
        var verifier = new StubVerifier(_ => Task.FromException<bool>(new TaskCanceledException(
            "request timed out",
            new Api.RequestTimeoutException(Api.RequestTimeoutPhase.ControlPlane, TimeSpan.FromSeconds(30)),
            CancellationToken.None)));
        var service = CreateService(verifier);

        Assert.False(await VerifyAsync(service));
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualRevocationOrSensitiveClear_CancelsPendingVerification(bool clearSensitive)
    {
        var completion = NewCompletion();
        var verifier = new StubVerifier(_ => completion.Task);
        var service = CreateService(verifier);
        var operation = VerifyAsync(service);

        if (clearSensitive)
            service.ClearSensitive();
        else
            service.EndGracePeriod();

        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(verifier.LastToken.IsCancellationRequested);
        completion.SetResult(true);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        Assert.True(await VerifyAsync(service));
        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task ModeChangeDuringVerification_RejectsSuccessAndCancelsItsGeneration()
    {
        var mode = 1;
        var completion = NewCompletion();
        var verifier = new StubVerifier(_ => completion.Task);
        var service = CreateService(verifier, readMode: () => mode);
        var operation = VerifyAsync(service);

        mode = 0;
        completion.SetResult(true);

        Assert.False(await operation);
        Assert.True(verifier.LastToken.IsCancellationRequested);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        mode = 1;
        Assert.True(await VerifyAsync(service));
        Assert.Equal(2, verifier.Calls);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("logout")]
    [InlineData("switch")]
    public async Task SessionChangesDuringVerification_RejectLateSuccess(string transition)
    {
        var session = UnlockedSession();
        var completion = NewCompletion();
        var verifier = new StubVerifier(_ => completion.Task);
        var service = CreateService(verifier, session);
        var operation = VerifyAsync(service);

        ChangeSession(session, transition);

        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(verifier.LastToken.IsCancellationRequested);
        completion.SetResult(true);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("logout")]
    [InlineData("switch")]
    public async Task SessionChanges_RevokeCachedApproval(string transition)
    {
        var session = UnlockedSession();
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier, session);
        Assert.True(await VerifyAsync(service));

        ChangeSession(session, transition);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        if (transition == "logout")
            session.BeginAccountSession("account-a", "a@example.com", Origin);
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));

        Assert.True(await VerifyAsync(service));
        Assert.Equal(2, verifier.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoSessionOrLockedSession_NeverInvokesVerifier(bool hasAccount)
    {
        var session = new VaultSession();
        if (hasAccount)
        {
            session.BeginAccountSession("account-a", "a@example.com", Origin);
            session.Lock();
        }
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier, session);

        Assert.False(await VerifyAsync(service));
        Assert.Equal(0, verifier.Calls);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task GracePeriodChanged_ReportsCreationRevocationAndObservedExpiry()
    {
        var clock = new ManualTimeProvider();
        var service = CreateService(SuccessfulVerifier(), clock: clock);
        var changes = 0;
        service.GracePeriodChanged += (_, _) => changes++;

        Assert.True(await VerifyAsync(service));
        service.EndGracePeriod();
        Assert.True(await VerifyAsync(service));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);

        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task ThrowingCancellationCallback_CannotPreventManualRevocation()
    {
        var completion = NewCompletion();
        var verifier = new StubVerifier(ct =>
        {
            ct.Register(() => throw new InvalidOperationException("Failed cancellation callback."));
            return completion.Task;
        });
        var service = CreateService(verifier);
        var operation = VerifyAsync(service);

        service.EndGracePeriod();

        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        completion.SetResult(true);
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task RevocationTakesEffectBeforeCancellationReachesAllPendingVerifiers()
    {
        var firstCompletion = NewCompletion();
        var secondCompletion = NewCompletion();
        var callbackEntered = NewCompletion();
        var releaseCallback = NewCompletion();
        var calls = 0;
        var verifier = new StubVerifier(ct =>
        {
            if (++calls == 1)
                return firstCompletion.Task;
            ct.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            return secondCompletion.Task;
        });
        var service = CreateService(verifier);
        var firstOperation = VerifyAsync(service);
        var secondOperation = VerifyAsync(service);
        var revocation = Task.Run(service.EndGracePeriod);

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstCompletion.SetResult(true);

            Assert.False(await firstOperation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        }
        finally
        {
            releaseCallback.TrySetResult(true);
            await revocation.WaitAsync(TimeSpan.FromSeconds(5));
            secondCompletion.TrySetResult(true);
        }

        Assert.False(await secondOperation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
    }

    [Fact]
    public async Task CancellationFromGraceNotification_RemovesTheUnsuccessfulRequestsApproval()
    {
        using var cancellation = new CancellationTokenSource();
        var verifier = SuccessfulVerifier();
        var service = CreateService(verifier);
        service.GracePeriodChanged += (_, _) =>
        {
            if (service.GraceRemaining > TimeSpan.Zero)
                cancellation.Cancel();
        };

        Assert.False(await VerifyAsync(service, cancellation.Token));
        Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        Assert.True(await VerifyAsync(service));
        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task CallerCancellationTakesEffectBeforeLinkedTokenReceivesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = NewCompletion();
        var callbackEntered = NewCompletion();
        var releaseCallback = NewCompletion();
        var verifier = new StubVerifier(_ => completion.Task);
        var service = CreateService(verifier);
        var operation = VerifyAsync(service, cancellation.Token);
        using var registration = cancellation.Token.Register(() =>
        {
            callbackEntered.TrySetResult(true);
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        var cancelTask = Task.Run(cancellation.Cancel);

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(verifier.LastToken.IsCancellationRequested);
            completion.SetResult(true);

            Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(TimeSpan.Zero, service.GraceRemaining);
        }
        finally
        {
            releaseCallback.TrySetResult(true);
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static readonly ServerOrigin Origin = new("https://vault.example");

    private static VaultSession UnlockedSession()
    {
        var session = new VaultSession();
        session.BeginAccountSession("account-a", "a@example.com", Origin);
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        return session;
    }

    private static void ChangeSession(VaultSession session, string transition)
    {
        switch (transition)
        {
            case "lock":
                session.Lock();
                break;
            case "logout":
                session.Logout();
                break;
            case "switch":
                session.BeginAccountSession("account-b", "b@example.com", Origin);
                session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transition));
        }
    }

    private static SensitiveActionVerificationService CreateService(
        StubVerifier verifier,
        VaultSession? session = null,
        ManualTimeProvider? clock = null,
        Func<int>? readMode = null) =>
        new(verifier, session ?? UnlockedSession(), clock ?? new ManualTimeProvider(), readMode ?? (() => 1));

    private static Task<bool> VerifyAsync(
        SensitiveActionVerificationService service,
        CancellationToken ct = default) =>
        service.VerifyAsync(0, "Verify sensitive action", _ => Task.FromResult<string?>(null), ct);

    private static StubVerifier SuccessfulVerifier() => new(_ => Task.FromResult(true));

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class StubVerifier(Func<CancellationToken, Task<bool>> verify) : ISensitiveActionVerifier
    {
        public int Calls { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<bool> VerifyAsync(
            nint hwnd,
            string message,
            Func<CancellationToken, Task<string?>> prompt,
            CancellationToken ct = default)
        {
            Calls++;
            LastToken = ct;
            return verify(ct);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
