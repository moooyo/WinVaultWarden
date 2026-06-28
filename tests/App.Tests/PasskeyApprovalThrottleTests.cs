using App.Services;
using Xunit;

namespace App.Tests;

public class PasskeyApprovalThrottleTests
{
    [Fact]
    public void TryBegin_FirstCallForOrigin_Allowed()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("https://github.com"));
    }

    [Fact]
    public void TryBegin_SecondCallWithinCooldown_Blocked()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("https://github.com"));
        now += TimeSpan.FromMilliseconds(500);
        Assert.False(throttle.TryBegin("https://github.com"));
    }

    [Fact]
    public void TryBegin_AfterCooldownElapsed_AllowedAgain()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("https://github.com"));
        now += TimeSpan.FromSeconds(3);
        Assert.True(throttle.TryBegin("https://github.com"));
    }

    [Fact]
    public void TryBegin_DifferentOrigins_IndependentWindows()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("https://github.com"));
        Assert.True(throttle.TryBegin("https://example.com"));
    }

    [Fact]
    public void TryBegin_PrunesStaleOrigins()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        // Register several distinct origins at t0.
        Assert.True(throttle.TryBegin("https://a.example"));
        Assert.True(throttle.TryBegin("https://b.example"));
        Assert.True(throttle.TryBegin("https://c.example"));
        Assert.Equal(3, throttle.TrackedOriginCount);

        // Advance past the cooldown so all three become stale, then prompt one new origin.
        now += TimeSpan.FromSeconds(3);
        Assert.True(throttle.TryBegin("https://d.example"));

        // The three stale entries were pruned; only the fresh origin remains.
        Assert.Equal(1, throttle.TrackedOriginCount);
    }
}
