using App.Services;
using Core.Models;
using Core.Session;
using Xunit;

namespace App.Tests;

public class VaultTimeoutServiceTests
{
    private sealed class Clock { public DateTimeOffset Now = new(2026,1,1,12,0,0,TimeSpan.Zero); }

    private sealed class MutableSnapshot : IVaultSnapshot
    {
        public VaultState State => VaultState.Unlocked;
        public IReadOnlyList<Cipher> Ciphers => [];
        public IReadOnlyList<Folder> Folders => [];
        public IReadOnlyList<DeviceInfo> Devices => [];
        public IReadOnlyList<VaultOrganizationPolicy> Policies { get; set; } = [];
        public IReadOnlyList<VaultOrganizationPolicyContext> PolicyOrganizations { get; set; } = [];
        public AccountInfo Account => AccountInfo.Empty;
        public int SkippedCipherCount => 0;
        public int SkippedFolderCount => 0;
        public bool DataStale => false;
        public long Generation => 1;
    }

    private static (
        VaultTimeoutService svc,
        Clock clock,
        MutableSnapshot snapshot,
        List<VaultTimeoutAction> fired) New()
    {
        var clock = new Clock();
        var svc = new VaultTimeoutService(() => clock.Now);
        var snapshot = new MutableSnapshot();
        var fired = new List<VaultTimeoutAction>();
        svc.TimeoutRequested += (_, a) => fired.Add(a);
        return (svc, clock, snapshot, fired);
    }

    [Fact]
    public void Idle_Expired_Tick_Fires_ConfiguredAction()
    {
        var (svc, clock, snapshot, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddMinutes(15);
        svc.Tick(snapshot, sessionTimeoutIndex: 3, timeoutActionIndex: 1);
        Assert.Equal(new[] { VaultTimeoutAction.Logout }, fired);
        Assert.False(svc.IsArmed);            // disarmed after firing
    }

    [Fact]
    public void NotEnoughIdle_Tick_DoesNotFire()
    {
        var (svc, clock, snapshot, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddMinutes(14);
        svc.Tick(snapshot, sessionTimeoutIndex: 3, timeoutActionIndex: 0);
        Assert.Empty(fired);
    }

    [Fact]
    public void NotifyActivity_Resets_Idle()
    {
        var (svc, clock, snapshot, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddMinutes(14);
        svc.NotifyActivity();                 // reset (throttle window is 2s; 14min passed → updates)
        clock.Now = clock.Now.AddMinutes(14); // 14 since reset, < 15
        svc.Tick(snapshot, sessionTimeoutIndex: 3, timeoutActionIndex: 0);
        Assert.Empty(fired);
    }

    [Fact]
    public void LockNow_AlwaysFiresLock_EvenIfLogoutConfigured()
    {
        var (svc, _, _, fired) = New();
        svc.Start();
        svc.LockNow();
        Assert.Equal(new[] { VaultTimeoutAction.Lock }, fired);
    }

    [Fact]
    public void NotStarted_Or_Stopped_DoesNotFire()
    {
        var (svc, clock, snapshot, fired) = New();
        clock.Now = clock.Now.AddMinutes(60);
        svc.Tick(snapshot, sessionTimeoutIndex: 3, timeoutActionIndex: 0);
        svc.LockNow();
        svc.Start(); svc.Stop();
        svc.Tick(snapshot, sessionTimeoutIndex: 3, timeoutActionIndex: 0);
        svc.LockNow();
        Assert.Empty(fired);
    }

    [Fact]
    public void Never_Timeout_NeverFires()
    {
        var (svc, clock, snapshot, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddHours(10);
        svc.Tick(snapshot, IdleTimeoutPolicy.NeverIndex, timeoutActionIndex: 0);
        Assert.Empty(fired);
    }

    [Fact]
    public void PolicyBecomingStricter_IsEnforcedOnTheNextTick()
    {
        var (svc, clock, snapshot, fired) = New();
        svc.Start();
        clock.Now = clock.Now.AddMinutes(6);
        svc.Tick(snapshot, sessionTimeoutIndex: 5, timeoutActionIndex: 0);
        Assert.Empty(fired);

        snapshot.PolicyOrganizations =
        [
            new VaultOrganizationPolicyContext("org", 2, 1, true, true, false),
        ];
        snapshot.Policies =
        [
            new VaultOrganizationPolicy(
                "policy",
                "org",
                9,
                true,
                "{\"minutes\":5,\"action\":\"logOut\"}"),
        ];

        svc.Tick(snapshot, sessionTimeoutIndex: 5, timeoutActionIndex: 0);

        Assert.Equal([VaultTimeoutAction.Logout], fired);
        Assert.False(svc.IsArmed);
    }

    [Fact]
    public void ImmediatePolicy_FiresOnTheNextTickWithoutGracePeriod()
    {
        var (svc, _, snapshot, fired) = New();
        snapshot.PolicyOrganizations =
        [
            new VaultOrganizationPolicyContext("org", 2, 1, true, true, false),
        ];
        snapshot.Policies =
        [
            new VaultOrganizationPolicy(
                "policy",
                "org",
                9,
                true,
                "{\"type\":\"immediately\",\"minutes\":0,\"action\":\"logOut\"}"),
        ];
        svc.Start();

        svc.Tick(snapshot, IdleTimeoutPolicy.NeverIndex, timeoutActionIndex: 0);

        Assert.Equal([VaultTimeoutAction.Logout], fired);
        Assert.False(svc.IsArmed);
    }

    [Fact]
    public void MalformedPolicy_DoesNotBecomeAnImmediateTimeout()
    {
        var (svc, clock, snapshot, fired) = New();
        snapshot.Policies =
        [
            new VaultOrganizationPolicy("policy", "missing-org", 9, true, "not-json"),
        ];
        svc.Start();

        svc.Tick(snapshot, IdleTimeoutPolicy.NeverIndex, timeoutActionIndex: 0);
        Assert.Empty(fired);

        clock.Now = clock.Now.AddMinutes(15);
        svc.Tick(snapshot, IdleTimeoutPolicy.NeverIndex, timeoutActionIndex: 0);
        Assert.Equal([VaultTimeoutAction.Lock], fired);
    }

    [Fact]
    public void SystemLock_UsesPolicyRequiredLogoutAction()
    {
        var (svc, _, snapshot, fired) = New();
        snapshot.PolicyOrganizations =
        [
            new VaultOrganizationPolicyContext("org", 2, 2, true, true, false),
        ];
        snapshot.Policies =
        [
            new VaultOrganizationPolicy(
                "policy",
                "org",
                9,
                true,
                "{\"type\":\"onSystemLock\",\"minutes\":0,\"action\":\"logOut\"}"),
        ];
        svc.Start();

        svc.HandleSystemLock(
            snapshot,
            IdleTimeoutPolicy.OnSystemLockIndex,
            timeoutActionIndex: 0);

        Assert.Equal([VaultTimeoutAction.Logout], fired);
    }

    [Fact]
    public void SystemLock_PreservesUserLogoutUnderOnAppRestartPolicy()
    {
        var (svc, _, snapshot, fired) = New();
        snapshot.PolicyOrganizations =
        [
            new VaultOrganizationPolicyContext("org", 2, 2, true, true, false),
        ];
        snapshot.Policies =
        [
            new VaultOrganizationPolicy(
                "policy",
                "org",
                9,
                true,
                "{\"type\":\"onAppRestart\",\"minutes\":0}"),
        ];
        svc.Start();

        svc.HandleSystemLock(
            snapshot,
            IdleTimeoutPolicy.OnSystemLockIndex,
            timeoutActionIndex: 1);

        Assert.Equal([VaultTimeoutAction.Logout], fired);
    }
}
