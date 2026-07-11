using Core.Passkeys;
using Xunit;

namespace BrowserNativeHost.Tests;

public class PasskeyRequestGateTests
{
    [Fact]
    public void SameFrameAndRp_AllowsOneInFlight()
    {
        var gate = new PasskeyRequestGate(TimeProvider.System);
        var key = new PasskeyRequestKey(42, 7, "example.com");

        using var first = gate.TryEnter(key, out var firstError);
        using var second = gate.TryEnter(key, out var secondError);

        Assert.NotNull(first);
        Assert.Null(firstError);
        Assert.Null(second);
        Assert.Equal(PasskeyErrorCodes.RequestInProgress, secondError);
    }

    [Fact]
    public void ReleasingLease_AllowsTheNextRequest()
    {
        var gate = new PasskeyRequestGate(TimeProvider.System);
        var key = new PasskeyRequestKey(1, 0, "example.com");

        var first = gate.TryEnter(key, out var firstError);
        Assert.NotNull(first);
        Assert.Null(firstError);
        first!.Dispose();

        using var second = gate.TryEnter(key, out var secondError);
        Assert.NotNull(second);
        Assert.Null(secondError);
    }

    [Fact]
    public void DifferentFrameOrRp_AreIndependent()
    {
        var gate = new PasskeyRequestGate(TimeProvider.System);

        using var a = gate.TryEnter(new PasskeyRequestKey(1, 0, "example.com"), out _);
        using var b = gate.TryEnter(new PasskeyRequestKey(2, 0, "example.com"), out _);
        using var c = gate.TryEnter(new PasskeyRequestKey(1, 1, "example.com"), out _);
        using var d = gate.TryEnter(new PasskeyRequestKey(1, 0, "other.com"), out _);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(c);
        Assert.NotNull(d);
    }

    [Fact]
    public void FourthRequestWithinTenSeconds_IsRateLimited()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var gate = new PasskeyRequestGate(clock);
        var key = new PasskeyRequestKey(42, 7, "example.com");

        for (var index = 0; index < 3; index++)
        {
            using var lease = gate.TryEnter(key, out var error);
            Assert.NotNull(lease);
            Assert.Null(error);
        }

        Assert.Null(gate.TryEnter(key, out var limited));
        Assert.Equal(PasskeyErrorCodes.RateLimited, limited);

        clock.Advance(TimeSpan.FromSeconds(10));
        using var afterWindow = gate.TryEnter(key, out var afterError);
        Assert.NotNull(afterWindow);
        Assert.Null(afterError);
    }

    [Fact]
    public void RateLimitWindow_SlidesAsTimeAdvances()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
        var gate = new PasskeyRequestGate(clock);
        var key = new PasskeyRequestKey(3, 0, "example.com");

        for (var index = 0; index < 3; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            using var lease = gate.TryEnter(key, out var error);
            Assert.NotNull(lease);
            Assert.Null(error);
        }

        // Three timestamps live at t=2,4,6. At t=6 a fourth is still limited.
        Assert.Null(gate.TryEnter(key, out var limited));
        Assert.Equal(PasskeyErrorCodes.RateLimited, limited);

        // Advancing to t=12 expires the t=2 sample (window is [t-10, t]); one slot frees up.
        clock.Advance(TimeSpan.FromSeconds(6));
        using var afterWindow = gate.TryEnter(key, out var afterError);
        Assert.NotNull(afterWindow);
        Assert.Null(afterError);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
