using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

public partial class DevicesViewModel : ObservableObject
{
    public ObservableCollection<DeviceItem> Devices { get; } = new();

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasNoDevices => Devices.Count == 0;

    public DevicesViewModel(IDeviceUiService service)
    {
        foreach (var d in service.GetDevices()) Devices.Add(d);
    }

    // 撤销:从列表移除(当前设备不可撤销)。
    [RelayCommand]
    private void Revoke(DeviceItem? device)
    {
        if (device is { IsCurrent: false } && Devices.Remove(device))
            OnPropertyChanged(nameof(HasNoDevices));
    }
}
