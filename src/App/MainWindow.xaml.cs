using App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using App.Services;
using Core.Services;
using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Windows.Foundation;
using Windows.Graphics;

namespace App;

public sealed partial class MainWindow : Window
{
    private const double LoginWindowInitialWidthDip = 440;
    private const double LoginWindowInitialHeightDip = 540;
    private const double LoginWindowMeasureWidthDip = 440;
    private const double LoginWindowMinWidthDip = 440;
    private const double LoginWindowMaxWidthDip = 520;
    private const double LoginWindowMinHeightDip = 500;
    private const double LoginWindowMaxHeightDip = 820;
    private const double LoginWindowWorkAreaMarginDip = 80;
    private const double LoginWindowVerticalBreathingRoomDip = 2;
    private const double DefaultVaultWindowWidthDip = 1180;
    private const double DefaultVaultWindowHeightDip = 760;

    private SizeInt32? _lastVaultWindowSize;
    private bool _hasShownVault;
    private bool _isLoginWindow;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        // 给系统标题栏按钮(最小化/最大化/关闭)留出右侧空间,避免与自定义内容重叠。
        SizeChanged += (_, _) => UpdateCaptionPadding();
        UpdateCaptionPadding();

        Activated += OnFirstActivated;
        ShowLogin();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        if (_isLoginWindow)
            RequestLoginWindowFit();
    }

    private void UpdateCaptionPadding()
    {
        // RightInset 是物理像素,转成 DIP(除以光栅缩放)。
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        RightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    private void PopulateFolderNavigation()
    {
        var vaultService = global::App.App.Services.GetRequiredService<IVaultUiService>();
        var folders = VaultNavigationService.BuildFolderItems(vaultService.GetFilters());
        FoldersNavItem.MenuItems.Clear();
        FoldersNavItem.Visibility = folders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var folder in folders)
        {
            FoldersNavItem.MenuItems.Add(new NavigationViewItem
            {
                Content = folder.Label,
                Tag = folder.Tag,
                Icon = new FontIcon { Glyph = folder.Glyph },
            });
        }
    }

    public void RefreshFolderNavigation() => PopulateFolderNavigation();

    /// <summary>供 NotificationsHost 在 VaultChanged 时调用：若当前页面是 VaultPage 则刷新列表。</summary>
    public void RefreshVaultList()
    {
        if (ContentFrame.Content is Views.VaultPage vaultPage)
            vaultPage.RefreshVaultList();
    }

    /// <summary>供 NotificationsHost 在 SendsChanged 时调用：若当前页面是 SendPage 则刷新列表。</summary>
    public void RefreshSendList()
    {
        if (ContentFrame.Content is Views.SendPage sendPage)
            sendPage.RefreshSendList();
    }

    /// <summary>供 NotificationsHost 在 AuthRequestsChanged 时调用：若当前页面是 DevicesPage 则刷新。</summary>
    public void RefreshRequestsList()
    {
        if (ContentFrame.Content is Views.DevicesPage devicesPage)
            devicesPage.RefreshRequestsList();
    }

    // 登录前:隐藏导航壳,只显示登录页(占满整窗,标题栏仅保留品牌)。
    public void ShowLogin()
    {
        ApplyLoginWindowLayout();
        Nav.Visibility = Visibility.Collapsed;
        LoginHost.Visibility = Visibility.Visible;
        LoginFrame.Navigate(typeof(LoginPage));
    }

    // 登录成功:显示主导航壳。
    public void ShowVault()
    {
        ApplyVaultWindowLayout();
        PopulateFolderNavigation();
        LoginHost.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;
        Nav.SelectedItem = AllItemsNavItem;
        ContentFrame.Navigate(typeof(VaultPage), "vault:allitems");
        // 保险库已就绪，启动 WebSocket 推送（最佳努力）。
        _ = global::App.App.Services.GetRequiredService<Services.NotificationsHost>().StartAsync();
    }

    private void ApplyLoginWindowLayout()
    {
        if (_hasShownVault && !_isLoginWindow)
            _lastVaultWindowSize = AppWindow.Size;

        _isLoginWindow = true;
        ResizeClientDip(LoginWindowInitialWidthDip, LoginWindowInitialHeightDip);
    }

    public void RequestLoginWindowFit()
    {
        if (LoginFrame.Content is LoginPage loginPage)
            loginPage.RequestWindowFit();
    }

    public void FitLoginWindowToContent(FrameworkElement loginContent, Thickness contentPadding)
    {
        if (!_isLoginWindow || loginContent.XamlRoot is null)
            return;

        var horizontalPadding = contentPadding.Left + contentPadding.Right;
        var verticalPadding = contentPadding.Top + contentPadding.Bottom;
        var contentMeasureWidth = Math.Max(1, LoginWindowMeasureWidthDip - horizontalPadding);

        loginContent.Measure(new Size(contentMeasureWidth, double.PositiveInfinity));

        var titleBarHeight = AppTitleBar.ActualHeight > 0 ? AppTitleBar.ActualHeight : AppTitleBar.Height;
        var desiredWidth = Math.Clamp(
            Math.Ceiling(loginContent.DesiredSize.Width + horizontalPadding),
            LoginWindowMinWidthDip,
            LoginWindowMaxWidthDip);
        var desiredHeight = Math.Clamp(
            Math.Ceiling(titleBarHeight + loginContent.DesiredSize.Height + verticalPadding + LoginWindowVerticalBreathingRoomDip),
            LoginWindowMinHeightDip,
            GetLoginWindowMaxHeightDip());

        ResizeClientDip(desiredWidth, desiredHeight);
    }

    private double GetLoginWindowMaxHeightDip()
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var workAreaHeightDip = workArea.Height / scale;

        return Math.Max(
            LoginWindowMinHeightDip,
            Math.Min(LoginWindowMaxHeightDip, workAreaHeightDip - LoginWindowWorkAreaMarginDip));
    }

    private void ApplyVaultWindowLayout()
    {
        _isLoginWindow = false;
        _hasShownVault = true;

        if (_lastVaultWindowSize is { Width: > 0, Height: > 0 } size)
            AppWindow.Resize(size);
        else
            ResizeWindowDip(DefaultVaultWindowWidthDip, DefaultVaultWindowHeightDip);
    }

    private void ResizeWindowDip(double width, double height)
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale))));
    }

    private void ResizeClientDip(double width, double height)
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.ResizeClient(new SizeInt32(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale))));
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
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage));
                break;
            case "devices":
                ContentFrame.Navigate(typeof(DevicesPage));
                break;
            case "reports":
                ContentFrame.Navigate(typeof(SecurityReportPage));
                break;
            case "lock":
                await global::App.App.Services.GetRequiredService<Services.NotificationsHost>().StopAsync();
                await global::App.App.Services.GetRequiredService<IAuthService>().LockAsync();
                ShowLogin();
                break;
            case "logout":
                if (await global::App.Views.DialogHelper.ConfirmAsync(
                        Nav.XamlRoot, "退出登录", "确定退出登录?将清除本地会话,需重新登录。", "退出登录"))
                {
                    _ = global::App.App.Services.GetRequiredService<Services.NotificationsHost>().StopAsync();
                    await global::App.App.Services.GetRequiredService<IAuthService>().LogoutAsync();
                    ShowLogin();
                }
                break;
            case "io":
                ContentFrame.Navigate(typeof(ImportExportPage));
                break;
        }
    }

    private async Task ShowGeneratorDialogAsync()
    {
        var dialog = new GeneratorDialog { XamlRoot = Nav.XamlRoot };
        await dialog.ShowAsync();
    }
}
