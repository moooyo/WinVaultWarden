using Core.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class SettingsViewModelPinTests
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
