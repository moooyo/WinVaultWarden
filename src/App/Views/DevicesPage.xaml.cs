using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class DevicesPage : Page
{
    public DevicesViewModel ViewModel { get; }

    public DevicesPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<DevicesViewModel>();
        InitializeComponent();
    }

    private async void OnRevokeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DeviceItem device })
            return;
        if (await DialogHelper.ConfirmAsync(
                XamlRoot, "撤销设备", $"确定要撤销"{device.Name}"吗?该设备的会话将被终止。", "撤销"))
        {
            ViewModel.RevokeCommand.Execute(device);
        }
    }
}
