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

    /// <summary>Refreshes requests after a notification on the UI thread.</summary>
    public void RefreshRequestsList() => _ = ViewModel.RefreshRequestsAsync();

    private async void OnApproveRequestClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as AuthRequestItem
                ?? (sender as FrameworkElement)?.DataContext as AuthRequestItem;
        if (item is null)
            return;
        await ViewModel.ApproveAsync(item, ConfirmFingerprintAsync, CancellationToken.None);
    }

    /// <summary>Requires an explicit fingerprint match before releasing the approval gate.</summary>
    private async Task<bool> ConfirmFingerprintAsync(AuthRequestItem item, CancellationToken ct)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = $"{item.DeviceTypeName} · {item.IpAddress}\n{item.CreatedLabel}",
            TextWrapping = TextWrapping.Wrap,
        });
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

        var verifiedBox = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "我已在请求设备上核对，五词指纹短语完全一致。",
                TextWrapping = TextWrapping.Wrap,
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(verifiedBox, "DevicesFingerprintVerifiedCheckBox");
        body.Children.Add(verifiedBox);

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "确认设备登录请求",
            Content = body,
            PrimaryButtonText = "确认并批准",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
        };
        verifiedBox.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        verifiedBox.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await DialogHelper.ShowTrackedAsync(dialog, ct: ct) == ContentDialogResult.Primary
            && verifiedBox.IsChecked == true;
    }

    private async void OnDenyRequestClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as AuthRequestItem
                ?? (sender as FrameworkElement)?.DataContext as AuthRequestItem;
        if (item is null)
            return;

        await ViewModel.DenyAsync(
            item,
            (request, ct) => DialogHelper.ConfirmAsync(
                XamlRoot,
                "拒绝登录请求",
                $"确定拒绝来自 {request.DeviceTypeName} 的登录请求吗？",
                "拒绝",
                ct: ct),
            CancellationToken.None);
    }
}
