using App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        // 给系统标题栏按钮(最小化/最大化/关闭)留出右侧空间,避免与自定义内容重叠。
        SizeChanged += (_, _) => UpdateCaptionPadding();
        UpdateCaptionPadding();

        // 启动先进登录页;登录成功后由 LoginPage 切到主导航。
        ShowLogin();
    }

    private void UpdateCaptionPadding()
    {
        // RightInset 是物理像素,转成 DIP(除以光栅缩放)。
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        RightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    // 登录前:隐藏导航壳,只显示登录页(占满整窗,标题栏仅保留品牌)。
    public void ShowLogin()
    {
        Nav.Visibility = Visibility.Collapsed;
        LoginHost.Visibility = Visibility.Visible;
        LoginFrame.Navigate(typeof(LoginPage));
    }

    // 登录成功:显示主导航壳。
    public void ShowVault()
    {
        LoginHost.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;
        Nav.SelectedItem = AllItemsNavItem;
        ContentFrame.Navigate(typeof(VaultPage), "vault:allitems");
    }

    private async void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item)
            return;

        await NavigateByTagAsync(item.Tag as string);
    }

    private async Task NavigateByTagAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        if (tag.StartsWith("vault:", StringComparison.Ordinal))
        {
            ContentFrame.Navigate(typeof(VaultPage), tag);
            return;
        }

        if (tag.StartsWith("send:", StringComparison.Ordinal))
        {
            ContentFrame.Navigate(typeof(SendPage), tag);
            return;
        }

        switch (tag)
        {
            case "gen":
                await ShowGeneratorDialogAsync();
                break;
            case "io":
                ContentFrame.Navigate(typeof(SimplePage), ("导入导出", "\uE8AB"));
                break;
        }
    }

    private async Task ShowGeneratorDialogAsync()
    {
        var dialog = new GeneratorDialog { XamlRoot = Nav.XamlRoot };
        await dialog.ShowAsync();
    }

    private void OnFooterSettingsClick(object sender, RoutedEventArgs e) =>
        ContentFrame.Navigate(typeof(SettingsPage));

    private void OnFooterDevicesClick(object sender, RoutedEventArgs e) =>
        ContentFrame.Navigate(typeof(DevicesPage));

    private void OnFooterLockClick(object sender, RoutedEventArgs e) => ShowLogin();

    private void OnLogout(object sender, RoutedEventArgs e) => ShowLogin();
}
