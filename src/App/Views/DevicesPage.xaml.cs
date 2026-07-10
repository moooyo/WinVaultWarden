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

    /// <summary>供 NotificationsHost 在收到 AuthRequestsChanged 推送后从 UI 线程调用。</summary>
    public void RefreshRequestsList() => _ = ViewModel.RefreshRequestsAsync();

    private async void OnApproveRequestClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as AuthRequestItem
                ?? (sender as FrameworkElement)?.DataContext as AuthRequestItem;
        if (item is null)
            return;
        await ViewModel.ApproveAsync(item, ConfirmFingerprintAsync);
    }

    /// <summary>
    /// 审批前弹窗：展示指纹短语，要求用户核对与请求设备显示的一致。
    /// 仅当用户点“确认并批准”才返回 true；取消/关闭返回 false → ViewModel 不加密下发。
    /// </summary>
    private async Task<bool> ConfirmFingerprintAsync(AuthRequestItem item)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = "请确认下方指纹短语与发起登录的设备上显示的完全一致。只有一致才批准；若不一致，可能是恶意或被篡改的设备正试图窃取你的密码库，请拒绝。",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = item.FingerprintPhrase,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 18,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "确认设备登录请求",
            Content = body,
            PrimaryButtonText = "确认并批准",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await DialogHelper.ShowTrackedAsync(dialog) == ContentDialogResult.Primary;
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
