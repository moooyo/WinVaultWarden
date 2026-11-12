using App.Interop;
using App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace App.Views;

/// <summary>Displays confirmed request metadata; it has no approval or vault-key operation.</summary>
public sealed partial class LockedDeviceRequestWindow : Window
{
    private readonly StackPanel _content;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _expiry = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _unlock = new() { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6C6598F975340F5A), HorizontalAlignment = HorizontalAlignment.Stretch };
    private LockedDeviceRequestNotice? _notice;
    private bool _closed;
    public Guid Epoch { get; }
    public string RequestId { get; }
    public bool WasCancelled { get; private set; }
    public bool IsClosed => _closed;
    public bool Represents(LockedDeviceRequestNotice notice) => ReferenceEquals(_notice, notice);

    public LockedDeviceRequestWindow(LockedDeviceRequestNotice notice, Func<bool> requestUnlock)
    {
        _notice = notice;
        Epoch = notice.Epoch;
        RequestId = notice.Request.Id;
        InitializeComponent();
        _content = MetadataPanel;
        Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA08EE0A51B4EF980);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        var root = Root;
        _content.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE4E9DE75197CF833),
            Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        _content.Children.Add(new TextBlock
        {
            Text = $"{notice.Account.Email}\n{notice.Account.Endpoints.WebVault.Value}",
            TextWrapping = TextWrapping.Wrap,
        });
        _content.Children.Add(new TextBlock
        {
            Text = $"{notice.Request.DeviceTypeName} · {notice.Request.IpAddress}\n{notice.Request.CreatedLabel}",
            TextWrapping = TextWrapping.Wrap,
        });
        _content.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text318324FBB0847D10), TextWrapping = TextWrapping.Wrap });
        _content.Children.Add(new TextBlock
        {
            Text = notice.Request.FingerprintPhrase,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
        });
        _content.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text58C77E26A44C8B60),
            TextWrapping = TextWrapping.Wrap,
        });
        _content.Children.Add(_expiry);
        AutomationProperties.SetName(_unlock, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8AA87B17AF11D7B5));
        _unlock.Click += (_, _) =>
        {
            if (_closed || _notice is null)
                return;
            if (requestUnlock())
                Close();
        };
        _content.Children.Add(_unlock);
        var later = new Button { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text73B6E48A1B552D11), HorizontalAlignment = HorizontalAlignment.Stretch };
        later.Click += (_, _) => Close();
        _content.Children.Add(later);
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => { _closed = true; _timer.Stop(); ClearSensitive(); };
        root.Loaded += (_, _) =>
        {
            var scale = root.XamlRoot?.RasterizationScale ?? 1;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var margin = (int)Math.Ceiling(16 * scale);
            var width = Math.Max(1, Math.Min((int)(460 * scale), area.Width - margin * 2));
            var height = Math.Max(1, Math.Min((int)(560 * scale), area.Height - margin * 2));
            AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
            AppWindow.Move(new Windows.Graphics.PointInt32(area.X + (area.Width - width) / 2,
                area.Y + (area.Height - height) / 2));
        };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 560));
        Refresh();
        if (!_closed)
            _timer.Start();
    }

    private void Refresh()
    {
        if (_closed || _notice is not { } notice)
            return;
        var remaining = notice.Request.ExpiresAt - DateTimeOffset.UtcNow;
        if (remaining is null || remaining <= TimeSpan.Zero)
        {
            CancelAndClose();
            return;
        }
        _expiry.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text89F9E7A4BFF03266,remaining.Value.Minutes,remaining.Value.Seconds);
        if (Content is FrameworkElement root)
            root.RequestedTheme = AppPreferences.Current.ThemeIndex switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.SetWindowDisplayAffinity(handle, AppPreferences.Current.AllowScreenshots
            ? NativeMethods.WDA_NONE : NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    public void ClearSensitive()
    {
        _notice = null;
        void ClearOnUiThread()
        {
            foreach (var text in _content.Children.OfType<TextBlock>()) text.Text = string.Empty;
            _unlock.IsEnabled = false;
            _content.Children.Clear();
        }
        if (DispatcherQueue.HasThreadAccess) ClearOnUiThread();
        else DispatcherQueue.TryEnqueue(ClearOnUiThread);
    }

    public void CancelAndClose()
    {
        WasCancelled = true;
        ClearSensitive();
        void CloseOnUiThread() { if (!_closed) Close(); }
        if (DispatcherQueue.HasThreadAccess) CloseOnUiThread();
        else DispatcherQueue.TryEnqueue(CloseOnUiThread);
    }
}
