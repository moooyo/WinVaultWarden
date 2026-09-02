using App.Services;
using App.ViewModels;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public sealed class MaximumVaultTimeoutAppPolicyTests : AppPreferencesTestBase
{
    [Fact]
    public void Resolve_ClampsNeverPreferenceToExactPolicyMinutes()
    {
        var snapshot = SnapshotWithPolicy("{\"minutes\":10}", role: 2);

        var result = IdleTimeoutPolicy.Resolve(
            snapshot,
            IdleTimeoutPolicy.NeverIndex,
            timeoutActionIndex: 0);

        Assert.Equal(VaultTimeoutTrigger.AfterIdle, result.Trigger);
        Assert.Equal(10, result.Minutes);
        Assert.Equal(VaultTimeoutAction.Lock, result.Action);
    }

    [Fact]
    public void Resolve_PreservesUserPreferenceWhenItIsStricter()
    {
        var snapshot = SnapshotWithPolicy("{\"minutes\":15}", role: 2);

        var result = IdleTimeoutPolicy.Resolve(
            snapshot,
            sessionTimeoutIndex: 2,
            timeoutActionIndex: 0);

        Assert.Equal(VaultTimeoutTrigger.AfterIdle, result.Trigger);
        Assert.Equal(5, result.Minutes);
    }

    [Fact]
    public void Resolve_OnAppRestartPolicyPreservesStricterSystemLockPreference()
    {
        var snapshot = SnapshotWithPolicy(
            "{\"type\":\"onAppRestart\",\"minutes\":0}",
            role: 2);

        var result = IdleTimeoutPolicy.Resolve(
            snapshot,
            IdleTimeoutPolicy.OnSystemLockIndex,
            timeoutActionIndex: 1);

        Assert.Equal(VaultTimeoutTrigger.OnSystemLock, result.Trigger);
        Assert.Equal(VaultTimeoutAction.Logout, result.Action);
        Assert.True(IdleTimeoutPolicy.IsSessionTimeoutIndexAllowed(
            snapshot,
            IdleTimeoutPolicy.OnSystemLockIndex));
        Assert.Equal(
            IdleTimeoutPolicy.OnSystemLockIndex,
            IdleTimeoutPolicy.NormalizeSessionTimeoutIndexForPolicy(
                snapshot,
                IdleTimeoutPolicy.OnSystemLockIndex));
    }

    [Fact]
    public void Resolve_EnforcesImmediatePolicyWithoutOneMinuteGrace()
    {
        var snapshot = SnapshotWithPolicy(
            "{\"type\":\"immediately\",\"minutes\":0}",
            role: 2);

        var result = IdleTimeoutPolicy.Resolve(
            snapshot,
            IdleTimeoutPolicy.NeverIndex,
            timeoutActionIndex: 0);

        Assert.Equal(VaultTimeoutTrigger.AfterIdle, result.Trigger);
        Assert.Equal(0, result.Minutes);
        Assert.Equal(
            IdleTimeoutPolicy.PolicySpecificMinutesIndex,
            IdleTimeoutPolicy.NormalizeSessionTimeoutIndexForPolicy(
                snapshot,
                IdleTimeoutPolicy.NeverIndex));

        var viewModel = new SettingsViewModel(new FakeVaultService(snapshot));
        viewModel.ApplyPolicyRestrictions();
        Assert.True(viewModel.HasPolicySpecificTimeoutOption);
        Assert.Equal("立即", viewModel.PolicySpecificTimeoutOptionText);
        Assert.False(viewModel.CanSelectOneMinuteTimeout);
        Assert.DoesNotContain("1 分钟", viewModel.MaximumVaultTimeoutPolicyHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_NormalizesDisplayWithoutOverwritingGlobalPreference()
    {
        AppPreferences.Current.SessionTimeoutIndex = IdleTimeoutPolicy.NeverIndex;
        var snapshot = SnapshotWithPolicy("{\"minutes\":10}", role: 2);
        var viewModel = new SettingsViewModel(new FakeVaultService(snapshot));

        viewModel.ApplyPolicyRestrictions();

        Assert.Equal(
            IdleTimeoutPolicy.PolicySpecificMinutesIndex,
            viewModel.SelectedSessionTimeoutIndex);
        Assert.Equal(IdleTimeoutPolicy.NeverIndex, AppPreferences.Current.SessionTimeoutIndex);
        Assert.True(viewModel.HasPolicySpecificTimeoutOption);
        Assert.Equal("10 分钟", viewModel.PolicySpecificTimeoutOptionText);
        Assert.True(viewModel.HasMaximumVaultTimeoutPolicy);
        Assert.False(viewModel.CanSelectNeverTimeout);
        Assert.False(viewModel.CanSelectFifteenMinuteTimeout);
        Assert.True(viewModel.CanSelectFiveMinuteTimeout);
    }

    [Fact]
    public void Settings_UsesPolicyActionWithoutOverwritingGlobalPreference()
    {
        AppPreferences.Current.TimeoutActionIndex = 0;
        var snapshot = SnapshotWithPolicy(
            "{\"minutes\":30,\"action\":\"logOut\"}",
            role: 1);
        var viewModel = new SettingsViewModel(new FakeVaultService(snapshot));

        viewModel.ApplyPolicyRestrictions();

        Assert.Equal(1, viewModel.SelectedTimeoutActionIndex);
        Assert.Equal(0, AppPreferences.Current.TimeoutActionIndex);
        Assert.False(viewModel.CanSelectTimeoutAction);
        Assert.Contains("登出", viewModel.MaximumVaultTimeoutPolicyHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Settings_RefreshRestoresPreferenceAfterPolicyIsRemoved()
    {
        AppPreferences.Current.SessionTimeoutIndex = IdleTimeoutPolicy.NeverIndex;
        var snapshot = SnapshotWithPolicy("{\"minutes\":10}", role: 2);
        var viewModel = new SettingsViewModel(new FakeVaultService(snapshot));
        viewModel.ApplyPolicyRestrictions();
        Assert.Equal(
            IdleTimeoutPolicy.PolicySpecificMinutesIndex,
            viewModel.SelectedSessionTimeoutIndex);

        snapshot.Policies = [];
        snapshot.PolicyOrganizations = [];
        viewModel.ApplyPolicyRestrictions();

        Assert.Equal(IdleTimeoutPolicy.NeverIndex, viewModel.SelectedSessionTimeoutIndex);
        Assert.True(viewModel.CanSelectNeverTimeout);
        Assert.False(viewModel.HasMaximumVaultTimeoutPolicy);
    }

    [Fact]
    public void Settings_DefensiveSetterRejectsDisallowedNeverValue()
    {
        AppPreferences.Current.SessionTimeoutIndex = 5;
        var snapshot = SnapshotWithPolicy("{\"minutes\":10}", role: 2);
        var viewModel = new SettingsViewModel(new FakeVaultService(snapshot));
        viewModel.ApplyPolicyRestrictions();

        viewModel.SelectedSessionTimeoutIndex = IdleTimeoutPolicy.NeverIndex;

        Assert.Equal(
            IdleTimeoutPolicy.PolicySpecificMinutesIndex,
            viewModel.SelectedSessionTimeoutIndex);
        Assert.Equal(5, AppPreferences.Current.SessionTimeoutIndex);
    }

    private static MutableSnapshot SnapshotWithPolicy(string dataJson, int role)
    {
        return new MutableSnapshot
        {
            PolicyOrganizations =
            [
                new VaultOrganizationPolicyContext("org", 2, role, true, true, false),
            ],
            Policies =
            [
                new VaultOrganizationPolicy("policy", "org", 9, true, dataJson),
            ],
        };
    }

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

    private sealed class FakeVaultService(IVaultSnapshot snapshot) : IVaultService
    {
        public IVaultSnapshot Snapshot { get; } = snapshot;
        public IReadOnlyList<Cipher> GetCiphers() => [];
        public IReadOnlyList<Folder> GetFolders() => [];
        public IReadOnlyList<DeviceInfo> GetDevices() => [];
        public VaultSessionLease CaptureLease() =>
            throw new InvalidOperationException("No session lease is available in this test.");
        public bool IsCurrent(VaultSessionLease lease) => false;
        public void ThrowIfExpired(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
    }
}
