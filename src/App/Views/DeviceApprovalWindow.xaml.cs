using System.Runtime.InteropServices;
using App.Interop;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Services;
using Core.Session;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Vault;
using Windows.Graphics;
using Windows.System;

namespace App.Views;

public enum DeviceApprovalOutcome { Deferred, Approved, Denied, Expired, Cancelled }

public sealed partial class DeviceApprovalWindow : Window
{
    private readonly VaultSession _session;
    private readonly Func<AuthRequestItem, bool, CancellationToken, Task<UiOperationResult>> _approve;
    private readonly Func<AuthRequestItem, CancellationToken, Task<UiOperationResult>> _deny;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly nint _handle;
    private CancellationTokenRegistration _sessionCancellation;
    private AuthRequestItem? _request;
    private bool _busy;
    private bool _closed;
    private bool _fitQueued;
    private bool _positioned;

    public string RequestId { get; }
    public int QueuedRequestCount { get; set; }
    public DeviceApprovalOutcome Outcome { get; private set; } = DeviceApprovalOutcome.Deferred;

    public DeviceApprovalWindow(
        AuthRequestItem request,
        VaultSession session,
        VaultSessionLease expectedSession,
        Func<AuthRequestItem, bool, CancellationToken, Task<UiOperationResult>> approve,
        Func<AuthRequestItem, CancellationToken, Task<UiOperationResult>> deny)
    {
        _request = request;
        RequestId = request.Id;
        _session = session;
        _approve = approve;
        _deny = deny;
        InitializeComponent();
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(RequestTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        RequestHeading.Text = $"{request.DeviceTypeName} 想登录你的账户";
        RequestSubtitle.Text = $"{request.IpAddress} · {request.CreatedLabel}";
        Fingerprint.Text = string.IsNullOrWhiteSpace(request.FingerprintPhrase)
            ? "无法验证此请求的指纹短语" : request.FingerprintPhrase;
        Root.PreviewKeyDown += OnPreviewKeyDown;
        Root.Loaded += OnContentLoaded;
        RequestBody.SizeChanged += OnContentSizeChanged;
        RequestFooter.SizeChanged += OnContentSizeChanged;
        _timer.Tick += OnTimerTick;
        Closed += OnClosed;
        SettingsViewModel.AllowScreenshotsSettingChanged += OnScreenshotSettingChanged;
        RefreshState();
        FitWindowToContent();
        _timer.Start();
        _sessionCancellation = expectedSession.CancellationToken.Register(CancelAndClose);
    }

    private void OnTimerTick(object? sender, object e) => RefreshState();

    private void OnContentLoaded(object sender, RoutedEventArgs e) => QueueFitWindowToContent();

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) => QueueFitWindowToContent();

    private void QueueFitWindowToContent()
    {
        if (_closed || _fitQueued)
            return;
        _fitQueued = DispatcherQueue.TryEnqueue(FitWindowToContent);
    }

    private void FitWindowToContent()
    {
        _fitQueued = false;
        if (_closed || _request is null)
            return;

        var scale = Math.Max(1, GetDpiForWindow(_handle)) / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var margin = (int)Math.Round(16 * scale);
        var availableWidth = Math.Max(1, area.Width - (2 * margin));
        var availableHeight = Math.Max(1, area.Height - (2 * margin));
        // Extended title bars overlap the native client boundary. Once loaded, use
        // the actual XAML surface to avoid counting the system title bar twice.
        var frameWidth = Math.Max(0, AppWindow.Size.Width - (Root.ActualWidth > 0
            ? Root.ActualWidth * scale : AppWindow.ClientSize.Width));
        var frameHeight = Math.Max(0, AppWindow.Size.Height - (Root.ActualHeight > 0
            ? Root.ActualHeight * scale : AppWindow.ClientSize.Height));
        var contentWidth = Math.Max(1, Math.Min(460, (availableWidth - frameWidth) / scale));
        var unconstrained = new Windows.Foundation.Size(contentWidth, double.PositiveInfinity);
        RequestBody.Measure(unconstrained);
        RequestFooter.Measure(unconstrained);
        var contentHeight = 32 + RequestBody.DesiredSize.Height + RequestFooter.DesiredSize.Height;
        var desired = new SizeInt32(
            Math.Min(availableWidth, (int)Math.Ceiling(contentWidth * scale + frameWidth)),
            Math.Min(availableHeight, (int)Math.Ceiling(contentHeight * scale + frameHeight)));

        if (Math.Abs(desired.Width - AppWindow.Size.Width) < 2
            && Math.Abs(desired.Height - AppWindow.Size.Height) < 2)
            return;

        var centerX = _positioned ? AppWindow.Position.X + AppWindow.Size.Width / 2 : area.X + area.Width / 2;
        var centerY = _positioned ? AppWindow.Position.Y + AppWindow.Size.Height / 2 : area.Y + area.Height / 2;
        AppWindow.Resize(desired);
        AppWindow.Move(new PointInt32(
            Math.Clamp(centerX - desired.Width / 2, area.X + margin, Math.Max(area.X + margin, area.X + area.Width - margin - desired.Width)),
            Math.Clamp(centerY - desired.Height / 2, area.Y + margin, Math.Max(area.Y + margin, area.Y + area.Height - margin - desired.Height))));
        _positioned = true;
    }

    private void RefreshState()
    {
        if (_closed || _request is not { } request)
            return;
        var remaining = request.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining is null || remaining <= TimeSpan.Zero)
        {
            Outcome = DeviceApprovalOutcome.Expired;
            CancelOperations();
            DispatcherQueue.TryEnqueue(() => { if (!_closed) Close(); });
            return;
        }
        var seconds = (long)Math.Ceiling(remaining.Value.TotalSeconds);
        ExpiryNotice.Text = $"{seconds / 60}:{seconds % 60:00} 后过期 · 关闭窗口可以稍后处理";
        if (QueuedRequestCount > 0)
            ExpiryNotice.Text += $" · 后面还有 {QueuedRequestCount} 个请求";
        var locked = _session.State != VaultState.Unlocked;
        LockedNotice.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        ApproveButton.Content = locked ? "解锁并批准" : "批准登录";
        ApproveButton.IsEnabled = !_busy && FingerprintReviewed.IsChecked == true
            && !string.IsNullOrWhiteSpace(request.FingerprintPhrase);
        DenyButton.IsEnabled = !_busy;
        FingerprintReviewed.IsEnabled = !_busy;
        FailureNotice.Visibility = FailureNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed;

        Root.RequestedTheme = AppPreferences.Current.ThemeIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        NativeMethods.SetWindowDisplayAffinity(_handle, AppPreferences.Current.AllowScreenshots
            ? NativeMethods.WDA_NONE : NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        QueueFitWindowToContent();
    }

    private void OnFingerprintReviewed(object sender, RoutedEventArgs e) => RefreshState();

    private void OnScreenshotSettingChanged(bool allow) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed)
            NativeMethods.SetWindowDisplayAffinity(_handle,
                allow ? NativeMethods.WDA_NONE : NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    });

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || _request is not { } request || FingerprintReviewed.IsChecked != true)
            return;
        await DecideAsync(() => _approve(request, true, _cancellation.Token), DeviceApprovalOutcome.Approved);
    }

    private async void OnDenyClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _closed || _request is not { } request)
            return;
        await DecideAsync(() => _deny(request, _cancellation.Token), DeviceApprovalOutcome.Denied);
    }

    private async Task DecideAsync(Func<Task<UiOperationResult>> decide, DeviceApprovalOutcome outcome)
    {
        _busy = true;
        FailureNotice.IsOpen = false;
        RefreshState();
        try
        {
            var result = await decide();
            if (_closed)
                return;
            if (result.IsSuccess)
            {
                Outcome = outcome;
                Close();
            }
            else if (result.Status == UiOperationStatus.Failed)
            {
                FailureNotice.Message = result.Error ?? "请求未完成，请重试。";
                FailureNotice.IsOpen = true;
            }
        }
        finally
        {
            _busy = false;
            RefreshState();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Approval has no keyboard default; an Enter intended for another app never approves it.
        if (e.Key == VirtualKey.Enter)
            e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;
        e.Handled = true;
        Close();
    }

    public void CancelOperations()
    {
        if (!_cancellation.IsCancellationRequested)
            _cancellation.Cancel();
    }

    public void CancelAndClose()
    {
        CancelOperations();
        void CloseOnUiThread()
        {
            if (_closed)
                return;
            Outcome = DeviceApprovalOutcome.Cancelled;
            ClearSensitive();
            Close();
        }
        if (DispatcherQueue.HasThreadAccess)
            CloseOnUiThread();
        else
            DispatcherQueue.TryEnqueue(CloseOnUiThread);
    }

    public void ClearSensitive()
    {
        _request = null;
        void ClearOnUiThread()
        {
            Fingerprint.Text = string.Empty;
            RequestHeading.Text = string.Empty;
            RequestSubtitle.Text = string.Empty;
            FingerprintReviewed.IsChecked = false;
            ApproveButton.IsEnabled = false;
        }
        if (DispatcherQueue.HasThreadAccess)
            ClearOnUiThread();
        else
            DispatcherQueue.TryEnqueue(ClearOnUiThread);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        CancelOperations();
        _sessionCancellation.Dispose();
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        SettingsViewModel.AllowScreenshotsSettingChanged -= OnScreenshotSettingChanged;
        Root.PreviewKeyDown -= OnPreviewKeyDown;
        Root.Loaded -= OnContentLoaded;
        RequestBody.SizeChanged -= OnContentSizeChanged;
        RequestFooter.SizeChanged -= OnContentSizeChanged;
        ClearSensitive();
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint windowHandle);
}
