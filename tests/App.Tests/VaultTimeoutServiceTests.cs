using App.Services;
using Xunit;

namespace App.Tests;

public class VaultTimeoutServiceTests
{
    private sealed class Clock { public DateTimeOffset Now = new(2026,1,1,12,0,0,TimeSpan.Zero); }

    private static (VaultTimeoutService svc, Clock clock, List<VaultTimeoutAction> fired) New()
    {
        var clock = new Clock();
        var svc = new VaultTimeoutService(() => clock.Now);
        var fired = new List<VaultTimeoutAction>();
        svc.TimeoutRequested += (_, a) => fired.Add(a);
        return (svc, clock, fired);
    }

    [Fact]
    public void Idle_Expired_Tick_Fires_ConfiguredAction()
    {
        var (svc, clock, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddMinutes(15);
        svc.Tick(15, VaultTimeoutAction.Logout);
        Assert.Equal(new[] { VaultTimeoutAction.Logout }, fired);
        Assert.False(svc.IsArmed);            // disarmed after firing
    }

    [Fact]
    public void NotEnoughIdle_Tick_DoesNotFire()
    {
        var (svc, clock, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddMinutes(14);
        svc.Tick(15, VaultTimeoutAction.Lock);
        Assert.Empty(fired);
    }

    [Fact]
    public void NotifyActivity_Resets_Idle()
    {
        var (svc, clock, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddMinutes(14);
        svc.NotifyActivity();                 // reset (throttle window is 2s; 14min passed → updates)
        clock.Now = clock.Now.AddMinutes(14); // 14 since reset, < 15
        svc.Tick(15, VaultTimeoutAction.Lock);
        Assert.Empty(fired);
    }

    [Fact]
    public void LockNow_AlwaysFiresLock_EvenIfLogoutConfigured()
    {
        var (svc, _, fired) = New();
        svc.Start();
        svc.LockNow();
        Assert.Equal(new[] { VaultTimeoutAction.Lock }, fired);
    }

    [Fact]
    public void NotStarted_Or_Stopped_DoesNotFire()
    {
        var (svc, clock, fired) = New();
        clock.Now = clock.Now.AddMinutes(60);
        svc.Tick(15, VaultTimeoutAction.Lock);   // not started
        svc.LockNow();
        svc.Start(); svc.Stop();
        svc.Tick(15, VaultTimeoutAction.Lock);   // stopped
        svc.LockNow();
        Assert.Empty(fired);
    }

    [Fact]
    public void Never_Timeout_NeverFires()
    {
        var (svc, clock, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddHours(10);
        svc.Tick(0, VaultTimeoutAction.Lock);    // minutes 0 = never
        Assert.Empty(fired);
    }
}
