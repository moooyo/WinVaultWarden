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

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshRequestsAsync();
    }

    private void OnRefreshRequestsClick(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshRequestsAsync();
    }

    private async void OnApproveRequestClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as AuthRequestItem
                ?? (sender as FrameworkElement)?.DataContext as AuthRequestItem;
        if (item is null)
            return;
        await ViewModel.ApproveAsync(item);
    }

    private async void OnDenyRequestClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as AuthRequestItem
                ?? (sender as FrameworkElement)?.DataContext as AuthRequestItem;
        if (item is null)
            return;
        await ViewModel.DenyAsync(item);
    }
}
