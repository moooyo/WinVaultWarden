using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class DevicesViewModelTests
{
    [Fact]
    public void HasNoDevices_FalseWhenDevicesLoaded()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService());

        Assert.NotEmpty(vm.Devices);
        Assert.False(vm.HasNoDevices);
    }

    [Fact]
    public void HasNoDevices_TrueWhenServiceReturnsNone()
    {
        var vm = new DevicesViewModel(new EmptyDeviceUiService());

        Assert.Empty(vm.Devices);
        Assert.True(vm.HasNoDevices);
    }

    [Fact]
    public void Revoke_RaisesHasNoDevicesWhenLastNonCurrentRemoved()
    {
        var vm = new DevicesViewModel(new SingleRevocableDeviceUiService());
        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DevicesViewModel.HasNoDevices)) raised = true; };

        vm.RevokeCommand.Execute(vm.Devices.First(d => !d.IsCurrent));

        Assert.True(raised);
        Assert.True(vm.HasNoDevices);
    }

    [Fact]
    public void IsBusy_And_Error_AreSettableAndDefault()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService());

        Assert.False(vm.IsBusy);
        Assert.Null(vm.Error);

        vm.IsBusy = true;
        vm.Error = "撤销失败";

        Assert.True(vm.IsBusy);
        Assert.Equal("撤销失败", vm.Error);
    }

    [Fact]
    public void HasError_FalseWhenErrorNull_TrueWhenErrorSet()
    {
        var vm = new DevicesViewModel(new MockDeviceUiService());
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        Assert.False(vm.HasError);

        vm.Error = "连接超时";

        Assert.True(vm.HasError);
        Assert.Contains(nameof(DevicesViewModel.HasError), notifications);

        vm.Error = null;

        Assert.False(vm.HasError);
    }

    private sealed class EmptyDeviceUiService : IDeviceUiService
    {
        public IReadOnlyList<DeviceItem> GetDevices() => Array.Empty<DeviceItem>();
    }

    private sealed class SingleRevocableDeviceUiService : IDeviceUiService
    {
        public IReadOnlyList<DeviceItem> GetDevices() => new[]
        {
            new DeviceItem("d2", "iPhone", "", "now", IsCurrent: false),
        };
    }
}
