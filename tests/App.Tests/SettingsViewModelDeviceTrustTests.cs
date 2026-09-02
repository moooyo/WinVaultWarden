using App.ViewModels;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

public sealed class SettingsViewModelDeviceTrustTests
{
    [Fact]
    public async Task TrustCurrentDevice_WhenCapabilityIsAvailable_UpdatesState()
    {
        var deviceTrust = new FakeDeviceTrustService { CanTrust = true };
        var viewModel = new SettingsViewModel(
            accountUi: null!,
            twoFactorUi: null!,
            deviceTrust: deviceTrust);

        Assert.True(viewModel.ShowTrustedDeviceSettings);
        Assert.True(viewModel.CanTrustCurrentDevice);
        Assert.False(viewModel.IsCurrentDeviceTrusted);

        var result = await viewModel.TrustCurrentDeviceAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(deviceTrust.TrustCalled);
        Assert.True(viewModel.IsCurrentDeviceTrusted);
        Assert.False(viewModel.CanTrustCurrentDevice);
        Assert.Equal("已信任", viewModel.TrustedDeviceActionText);
    }

    [Fact]
    public void TrustedDeviceSettings_WithoutServerCapability_RemainsHidden()
    {
        var viewModel = new SettingsViewModel(
            accountUi: null!,
            twoFactorUi: null!,
            deviceTrust: new FakeDeviceTrustService());

        Assert.False(viewModel.ShowTrustedDeviceSettings);
        Assert.False(viewModel.CanTrustCurrentDevice);
    }

    [Fact]
    public void TrustedDeviceAction_WhileBusyIsDisabledAndShowsProgress()
    {
        var viewModel = new SettingsViewModel(
            accountUi: null!,
            twoFactorUi: null!,
            deviceTrust: new FakeDeviceTrustService { CanTrust = true })
        {
            IsBusy = true,
        };

        Assert.False(viewModel.CanTrustCurrentDevice);
        Assert.Equal("正在建立信任", viewModel.TrustedDeviceActionText);
    }

    private sealed class FakeDeviceTrustService : IDeviceTrustService
    {
        public bool CanTrust { get; set; }
        public bool Trusted { get; set; }
        public bool TrustCalled { get; private set; }
        public bool CanTrustCurrentDevice => CanTrust && !Trusted;
        public bool IsCurrentDeviceTrusted => Trusted;

        public void SetCurrentSessionCapability(VaultSessionLease lease, bool supported)
        {
        }

        public bool TryDecryptUserKey(
            string accountId,
            ServerEndpoints endpoints,
            string deviceIdentifier,
            string encryptedPrivateKey,
            string encryptedUserKey,
            out byte[] userKey)
        {
            userKey = [];
            return false;
        }

        public Task TrustCurrentDeviceAsync(CancellationToken ct = default)
        {
            TrustCalled = true;
            Trusted = true;
            return Task.CompletedTask;
        }
    }
}
