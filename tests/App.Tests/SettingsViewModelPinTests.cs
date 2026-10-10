using Core.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

[Collection(TestCollectionNames.AppPreferences)]
public class SettingsViewModelPinTests : AppPreferencesTestBase
{
    private sealed class FakePin : IPinService
    {
        public bool IsPinSet { get; set; }
        public string? LastSet { get; private set; }
        public int Cleared { get; private set; }
        public void SetPin(string pin) { LastSet = pin; IsPinSet = true; }
        public void ClearPin() { Cleared++; IsPinSet = false; }
    }

    [Fact]
    public void V2Account_DisablesPinAndRejectsProtectorCreation()
    {
        var pin = new FakePin();
        var vm = new SettingsViewModel(pin, supportsLocalUnlock: () => false);

        Assert.False(vm.CanUsePinUnlock);
        Assert.Contains("V2", vm.PinUnlockDescription, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => vm.SetPin("123456"));
        Assert.Null(pin.LastSet);
        Assert.False(pin.IsPinSet);
    }

    [Fact]
    public void AccountKeyCapabilityChange_NotifiesPinEligibilityWithoutCreatingAProtector()
    {
        var supported = true;
        var vm = new SettingsViewModel(new FakePin(), () => supported);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.True(vm.CanUsePinUnlock);

        supported = false;
        vm.ApplyPolicyRestrictions();

        Assert.False(vm.CanUsePinUnlock);
        Assert.Contains(nameof(SettingsViewModel.CanUsePinUnlock), changed);
    }

    [Fact]
    public void IsPinSet_Delegates_And_SetClear_Work()
    {
        var pin = new FakePin();
        var vm = new SettingsViewModel(pin);
        Assert.False(vm.IsPinSet);
        vm.SetPin("1234");
        Assert.True(vm.IsPinSet);
        Assert.Equal("1234", pin.LastSet);
        vm.ClearPin();
        Assert.False(vm.IsPinSet);
        Assert.Equal(1, pin.Cleared);
    }
}
