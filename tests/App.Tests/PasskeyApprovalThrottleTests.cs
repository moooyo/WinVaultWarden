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
}
