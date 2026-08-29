using App.Interop;
using App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using App.Services;
using Core.Services;
using Core.Session;
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
    private const double LoginWindowMinWidthDip = 320;
    private const double LoginWindowMaxWidthDip = 520;
    private const double LoginWindowMinHeightDip = 500;
    private const double LoginWindowMaxHeightDip = 820;
    private const double LoginWindowWorkAreaMarginPerEdgeDip = 40;
    private const double LoginWindowVerticalBreathingRoomDip = 2;
    private const double DefaultVaultWindowWidthDip = 1180;
    private const double DefaultVaultWindowHeightDip = 760;
    private const double VaultWindowWorkAreaMarginDip = 24;

    private SizeInt32? _lastVaultWindowSize;
    private bool _hasShownVault;
    private bool _isLoginWindow;

    private readonly VaultTimeoutService _timeout =
        global::App.App.Services.GetRequiredService<VaultTimeoutService>();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleTimer;

    // 导航服务:集中所有 Frame 导航;构造时注入内容区(ContentFrame)与登录流(LoginFrame)两个 Frame。
    private readonly FrameNavigationService _nav =
        global::App.App.Services.GetRequiredService<FrameNavigationService>();

    // 全项目唯一的生命周期协调器:所有 lock/logout/exit 入口只请求 intent,禁止旁路直接 Lock/Logout/Exit。
    private readonly IAppLifecycleCoordinator _lifecycle =
        global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();

    private readonly IntPtr _hwnd;
    private Services.TrayIconService? _tray;
    private bool _realExit;

    public MainWindow()
    {
        InitializeComponent();
        // Frame 就绪后注入导航服务(须在任何导航如 ShowLogin 之前)。
        _nav.Initialize(ContentFrame, LoginFrame);
        // 窗口 DispatcherQueue 就绪后注入 UI 派发器(须在任何后台线程派发到 UI 之前)。
        global::App.App.Services.GetRequiredService<UiDispatcher>().Initialize(DispatcherQueue);
        _ = global::App.App.Services.GetRequiredService<SessionTerminationHost>();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        _timeout.TimeoutRequested += OnTimeoutRequested;

        _idleTimer = DispatcherQueue.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(10);
        _idleTimer.Tick += (_, _) => _timeout.Tick(
            IdleTimeoutPolicy.MinutesForIndex(AppPreferences.Current.SessionTimeoutIndex),
            IdleTimeoutPolicy.ActionForIndex(AppPreferences.Current.TimeoutActionIndex));

        // 输入活动:挂到根内容(覆盖全窗口),handledEventsToo 以捕获子控件已处理事件。
        if (Content is UIElement root)
        {
            root.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _timeout.NotifyActivity()), true);
            root.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _timeout.NotifyActivity()), true);
            root.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _timeout.NotifyActivity()), true);
            root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, _) => _timeout.NotifyActivity()), true);
        }

        // 最小化 → 立即锁定;若「最小化到托盘」则隐藏窗口(仍先锁定,更安全)。经协调器统一处理。
        AppWindow.Changed += OnAppWindowChanged;

        // 系统锁屏/切换会话 → 立即锁定(最佳努力:打包环境可能不触发)。
        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Closed += (_, _) =>
            {
                try { Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
            };
        }
        catch { /* 事件不可用则降级:其余触发照常 */ }

        // 给系统标题栏按钮(最小化/最大化/关闭)留出右侧空间,避免与自定义内容重叠。
        SizeChanged += (_, _) => UpdateCaptionPadding();
        UpdateCaptionPadding();

        // 系统托盘：取 HWND、建服务（回调恢复/锁定/退出），按设置增删图标。
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _tray = new Services.TrayIconService(_hwnd, RestoreFromTray, LockFromTray, ExitApp);
        ApplyTrayIconSetting();
        ViewModels.SettingsViewModel.ShowTrayIconSettingChanged += OnShowTrayIconSettingChanged;

        // 窗口捕获保护：按设置对窗口应用/解除 WDA_EXCLUDEFROMCAPTURE（S4）。开机即应用,并随设置变更即时更新。
        ApplyScreenshotSetting(AppPreferences.Current.AllowScreenshots);
        ViewModels.SettingsViewModel.AllowScreenshotsSettingChanged += OnAllowScreenshotsSettingChanged;

        // 关闭窗口 → 经协调器统一处理(关闭到托盘先锁定并隐藏;真实退出走 Exit intent 完成清理后退出)。
        AppWindow.Closing += OnAppWindowClosing;

        // 窗口关闭时清理托盘（NIM_DELETE + 还原 WndProc），并退订设置事件。
        Closed += (_, _) =>
        {
            ViewModels.SettingsViewModel.ShowTrayIconSettingChanged -= OnShowTrayIconSettingChanged;
            ViewModels.SettingsViewModel.AllowScreenshotsSettingChanged -= OnAllowScreenshotsSettingChanged;
            _tray?.Dispose();
            _tray = null;
        };

        Activated += OnFirstActivated;
        ShowLogin();
    }

    private async void OnTimeoutRequested(object? sender, VaultTimeoutAction action)
    {
        await _lifecycle.RequestAsync(action == VaultTimeoutAction.Logout
            ? AppLifecycleIntent.Logout
            : AppLifecycleIntent.Lock);
    }

    // 最小化 → 请求 Lock intent(协调器清理),再按设置隐藏到托盘。
    private async void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            return;

        await _lifecycle.RequestAsync(AppLifecycleIntent.Lock);
        if (Services.TrayPolicy.ShouldHideOnMinimize(
            AppPreferences.Current.ShowTrayIcon,
            AppPreferences.Current.MinimizeToTray))
            sender.Hide();
    }

    // 关闭窗口 → 关闭到托盘则先 Lock 再隐藏;否则请求 Exit intent(协调器清理后经 ExitAfterLifecycle 退出)。
    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_realExit)
            return;

        e.Cancel = true;
        if (Services.TrayPolicy.ShouldHideOnClose(
            AppPreferences.Current.ShowTrayIcon,
            AppPreferences.Current.CloseToTray))
        {
            await _lifecycle.RequestAsync(AppLifecycleIntent.Lock);
            sender.Hide();
            return;
        }

        await _lifecycle.RequestAsync(AppLifecycleIntent.Exit);
    }

    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
            DispatcherQueue.TryEnqueue(async () =>
                await _lifecycle.RequestAsync(AppLifecycleIntent.Lock));
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
        var folders = VaultNavItemBuilder.BuildFolderItems(vaultService.GetFilters());
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
        _timeout.Stop();
        _idleTimer?.Stop();

        ApplyLoginWindowLayout();
        Nav.Visibility = Visibility.Collapsed;
        LoginHost.Visibility = Visibility.Visible;
        _nav.GoToLoginPage();
    }

    // 生命周期协调器完成清理后重新展示登录页,并保持敏感 UI 关闭,直到 ShowVault 里 MarkSessionReady 重新开放。
    public void ShowLoginAfterLifecycle()
    {
        ShowLogin();
        SetSensitiveUiEnabled(false);
    }

    // 敏感操作 UI 门禁:锁屏/登出等生命周期过渡期间禁用主导航,避免过渡中触发新的敏感操作。
    public void SetSensitiveUiEnabled(bool enabled) => Nav.IsEnabled = enabled;

    // 生命周期协调器完成清理后真实退出进程(此前已阻断新敏感操作、清空会话与瞬态)。
    public void ExitAfterLifecycle()
    {
        _realExit = true;
        _tray?.Dispose();
        _tray = null;
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    // 登录成功:显示主导航壳。
    public bool ShowVault(VaultSessionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        return _lifecycle.TryActivateSession(() =>
            session.TryRunIfUnlocked(lease, () =>
        {
            ApplyVaultWindowLayout();
            PopulateFolderNavigation();
            LoginHost.Visibility = Visibility.Collapsed;
            Nav.Visibility = Visibility.Visible;
            Nav.SelectedItem = AllItemsNavItem;
            _nav.GoToVault("vault:allitems");
            _ = global::App.App.Services.GetRequiredService<Services.NotificationsHost>().StartAsync();

            if (AppPreferences.Current.AllowBrowserIntegration && !AboutInfo.IsPackaged)
                global::App.App.Services.GetRequiredService<Services.PasskeyBridgeServer>().Start();

            _timeout.Start();
            _idleTimer?.Start();
        }));
    }

    private void ApplyLoginWindowLayout()
    {
        if (_hasShownVault && !_isLoginWindow)
            _lastVaultWindowSize = AppWindow.Size;

        _isLoginWindow = true;
        ResizeLoginClientDip(LoginWindowInitialWidthDip, LoginWindowInitialHeightDip);
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
        var measureWindowSize = ClampLoginWindowSizeToWorkArea(
            LoginWindowMeasureWidthDip,
            LoginWindowMaxHeightDip);
        var contentMeasureWidth = Math.Max(1, measureWindowSize.Width - horizontalPadding);

        loginContent.Measure(new Size(contentMeasureWidth, double.PositiveInfinity));

        var titleBarHeight = AppTitleBar.ActualHeight > 0 ? AppTitleBar.ActualHeight : AppTitleBar.Height;
        var desiredWidth = Math.Ceiling(loginContent.DesiredSize.Width + horizontalPadding);
        var desiredHeight = Math.Ceiling(
            titleBarHeight
            + loginContent.DesiredSize.Height
            + verticalPadding
            + LoginWindowVerticalBreathingRoomDip);

        ResizeLoginClientDip(desiredWidth, desiredHeight);
    }

    private void ResizeLoginClientDip(double desiredWidth, double desiredHeight)
    {
        var size = ClampLoginWindowSizeToWorkArea(desiredWidth, desiredHeight);
        ResizeClientDip(size.Width, size.Height);
    }

    private WindowSizeDip ClampLoginWindowSizeToWorkArea(double desiredWidth, double desiredHeight)
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1.0;

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        return LoginWindowSizing.ClampToWorkArea(
            new WindowSizeDip(desiredWidth, desiredHeight),
            new WindowSizeDip(workArea.Width / scale, workArea.Height / scale),
            new WindowSizeDip(LoginWindowMinWidthDip, LoginWindowMinHeightDip),
            new WindowSizeDip(LoginWindowMaxWidthDip, LoginWindowMaxHeightDip),
            LoginWindowWorkAreaMarginPerEdgeDip);
    }

    private void ApplyVaultWindowLayout()
    {
        _isLoginWindow = false;
        _hasShownVault = true;

        if (_lastVaultWindowSize is { Width: > 0, Height: > 0 } size)
            AppWindow.Resize(ClampVaultWindowSizeToWorkArea(size));
        else
            ResizeWindowDip(DefaultVaultWindowWidthDip, DefaultVaultWindowHeightDip);
    }

    private void ResizeWindowDip(double width, double height)
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        var requestedSize = new SizeInt32(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
        AppWindow.Resize(ClampVaultWindowSizeToWorkArea(requestedSize));
    }

    private SizeInt32 ClampVaultWindowSizeToWorkArea(SizeInt32 requestedSize)
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var marginPixels = Math.Max(0, (int)Math.Round(VaultWindowWorkAreaMarginDip * scale));
        var maxWidth = Math.Max(1, workArea.Width - (2 * marginPixels));
        var maxHeight = Math.Max(1, workArea.Height - (2 * marginPixels));

        return new SizeInt32(
            Math.Clamp(requestedSize.Width, 1, maxWidth),
            Math.Clamp(requestedSize.Height, 1, maxHeight));
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
            _nav.GoToVault(tag);
            return;
        }

        if (tag.StartsWith("send:", StringComparison.Ordinal))
        {
            _nav.GoToSend(tag);
            return;
        }

        switch (tag)
        {
            case "gen":
                await ShowGeneratorDialogAsync();
                break;
            case "settings":
                _nav.GoToSettings();
                break;
            case "devices":
                _nav.GoToDevices();
                break;
            case "reports":
                _nav.GoToSecurityReport();
                break;
            case "lock":
                await _lifecycle.RequestAsync(AppLifecycleIntent.Lock);
                break;
            case "logout":
                if (await global::App.Views.DialogHelper.ConfirmAsync(
                    Nav.XamlRoot,
                    "退出登录",
                    "确定退出登录?将清除本地会话,需重新登录。",
                    "退出登录"))
                {
                    await _lifecycle.RequestAsync(AppLifecycleIntent.Logout);
                }
                break;
            case "io":
                _nav.GoToImportExport();
                break;
        }
    }

    private async Task ShowGeneratorDialogAsync()
    {
        // 复用同一 GeneratorDialog 实例:生成器与历史对话框顺序打开(不嵌套),
        // 查看历史后循环重开生成器,保留其已生成的口令/选项状态。历史对话框本身
        // 只关闭并回报 ClearRequested,清除确认在其关闭之后由本宿主再单独弹出,
        // 避免同一 XamlRoot 下两个 ContentDialog 同时 ShowAsync。
        // 历史关闭后须检查真实 VaultState 仍 Unlocked 再继续,
        // 不能只依赖 coordinator.IsTransitioning(过渡完成后该窗口已归 false)。
        var dialog = new GeneratorDialog { XamlRoot = Nav.XamlRoot };
        while (true)
        {
            await global::App.Views.DialogHelper.ShowTrackedAsync(dialog);
            if (!dialog.ConsumeHistoryRequest())
                break;

            var history = new GeneratorHistoryDialog(dialog.ViewModel) { XamlRoot = Nav.XamlRoot };
            await global::App.Views.DialogHelper.ShowTrackedAsync(history);
            if (!IsVaultUnlocked())
                break;

            if (history.ClearRequested
                && await global::App.Views.DialogHelper.ConfirmAsync(
                    Nav.XamlRoot,
                    "清除历史记录",
                    "确定要清除全部生成历史吗?此操作无法撤销。",
                    "清除"))
            {
                dialog.ViewModel.ClearHistoryCommand.Execute(null);
            }
        }
    }

    private static bool IsVaultUnlocked() =>
        global::App.App.Services.GetRequiredService<IVaultService>().Snapshot.State
            == VaultState.Unlocked;

    /// <summary>按 ShowTrayIcon 设置增删托盘图标。</summary>
    public void ApplyTrayIconSetting()
    {
        if (_tray is null)
            return;
        if (AppPreferences.Current.ShowTrayIcon)
            _tray.Show();
        else
            _tray.Hide();
    }

    private void OnShowTrayIconSettingChanged(bool value) =>
        DispatcherQueue.TryEnqueue(ApplyTrayIconSetting);

    /// <summary>按 AllowScreenshots 设置对窗口应用/解除捕获保护（S4）。
    /// 关闭"允许截图"→ WDA_EXCLUDEFROMCAPTURE：窗口内容在截图/录屏/屏幕共享中呈黑框。</summary>
    private void ApplyScreenshotSetting(bool allow)
    {
        Interop.NativeMethods.SetWindowDisplayAffinity(
            _hwnd,
            allow ? Interop.NativeMethods.WDA_NONE : Interop.NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    private void OnAllowScreenshotsSettingChanged(bool value) =>
        DispatcherQueue.TryEnqueue(() => ApplyScreenshotSetting(value));

    /// <summary>从托盘恢复窗口：显示、还原最小化、置前台。供托盘左键/「打开」与单实例重定向调用。</summary>
    public void RestoreFromTray()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show(true);
            (AppWindow.Presenter as OverlappedPresenter)?.Restore();
            NativeMethods.SetForegroundWindow(_hwnd);
        });
    }

    private void LockFromTray() =>
        DispatcherQueue.TryEnqueue(async () =>
        {
            await _lifecycle.RequestAsync(AppLifecycleIntent.Lock);
            RestoreFromTray();
        });

    private void ExitApp() =>
        DispatcherQueue.TryEnqueue(async () =>
            await _lifecycle.RequestAsync(AppLifecycleIntent.Exit));
}
