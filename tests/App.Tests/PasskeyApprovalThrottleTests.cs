using App.Services;
using Xunit;

namespace App.Tests;

public class PasskeyApprovalThrottleTests
{
    [Fact]
    public void TryBegin_FirstCallForRpId_Allowed()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("github.com"));
    }

    [Fact]
    public void TryBegin_SecondCallWithinCooldown_Blocked()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("github.com"));
        now += TimeSpan.FromMilliseconds(500);
        Assert.False(throttle.TryBegin("github.com"));
    }

    [Fact]
    public void TryBegin_SameRpIdFromRotatedSubdomainOrigins_StaysCooled()
    {
        // The dialog service keys the throttle by rpId. Two different origins
        // (a.github.com, b.github.com) collapse to rpId "github.com", so rotating
        // the subdomain origin can no longer bypass the cooldown.
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("github.com"));   // from https://a.github.com
        now += TimeSpan.FromMilliseconds(500);
        Assert.False(throttle.TryBegin("github.com"));  // from https://b.github.com — still cooled
    }

    [Fact]
    public void TryBegin_AfterCooldownElapsed_AllowedAgain()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("github.com"));
        now += TimeSpan.FromSeconds(3);
        Assert.True(throttle.TryBegin("github.com"));
    }

    [Fact]
    public void TryBegin_DifferentRpIds_IndependentWindows()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        Assert.True(throttle.TryBegin("github.com"));
        Assert.True(throttle.TryBegin("example.com"));
    }

    [Fact]
    public void TryBegin_PrunesStaleKeys()
    {
        var now = DateTimeOffset.UnixEpoch;
        var throttle = new PasskeyApprovalThrottle(TimeSpan.FromSeconds(2), () => now);

        // Register several distinct rpIds at t0.
        Assert.True(throttle.TryBegin("a.example"));
        Assert.True(throttle.TryBegin("b.example"));
        Assert.True(throttle.TryBegin("c.example"));
        Assert.Equal(3, throttle.TrackedKeyCount);

        // Advance past the cooldown so all three become stale, then prompt one new rpId.
        now += TimeSpan.FromSeconds(3);
        Assert.True(throttle.TryBegin("d.example"));

        // The three stale entries were pruned; only the fresh rpId remains.
        Assert.Equal(1, throttle.TrackedKeyCount);
    }
}
