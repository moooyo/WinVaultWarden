using App.Services;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vault;

namespace App.Views;

internal sealed partial class TotpSetupCredentialsPanel : Grid, IDisposable
{
    private readonly VaultSession _session;
    private readonly VaultSessionLease _expectedSession;
    private readonly IAppLifecycleCoordinator _lifecycle;
    private readonly IClipboardService _clipboard;
    private readonly CancellationTokenSource _cancellation;
    private readonly Image _qrImage;
    private readonly TextBlock _qrFallback;
    private readonly TextBlock _secretText;
    private readonly TextBlock _copyStatus;
    private readonly Button _copyButton;
    private readonly StackPanel _manualPanel;
    private IDisposable? _registration;
    private string _secret = string.Empty;
    private bool _isStacked;
    private int _isCleared;
    private int _isDisposed;

    private TotpSetupCredentialsPanel(VaultSessionLease expectedSession)
    {
        var services = global::App.App.Services;
        _session = services.GetRequiredService<VaultSession>();
        _expectedSession = expectedSession;
        _lifecycle = services.GetRequiredService<IAppLifecycleCoordinator>();
        _clipboard = services.GetRequiredService<IClipboardService>();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(expectedSession.CancellationToken);

        MinWidth = 0;
        ColumnSpacing = 20;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _qrImage = new Image
        {
            Width = 148,
            Height = 148,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAutomationId(_qrImage, "TotpSetupQrImage");
        AutomationProperties.SetName(_qrImage, "使用验证器应用扫描此二维码");
        _qrFallback = new TextBlock
        {
            Text = "二维码暂不可用，请手动输入密钥",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12),
            Visibility = Visibility.Collapsed,
        };
        var qrContainer = new Grid
        {
            Width = 148,
            Height = 148,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        qrContainer.Children.Add(_qrImage);
        qrContainer.Children.Add(_qrFallback);
        Children.Add(qrContainer);

        _secretText = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(_secretText, "TotpSetupSecretText");
        _copyButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE8C8",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16,
            },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            IsEnabled = false,
        };
        AutomationProperties.SetAutomationId(_copyButton, "TotpSetupCopySecretButton");
        AutomationProperties.SetName(_copyButton, "复制验证器设置密钥");
        ToolTipService.SetToolTip(_copyButton, "复制密钥");
        _copyButton.Click += OnCopySecretClick;
        _copyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(_copyStatus, "TotpSetupCopyStatusText");
        _manualPanel = new StackPanel { Spacing = 8, MinWidth = 0 };
        _manualPanel.Children.Add(new TextBlock
        {
            Text = "无法扫描？在应用里手动输入密钥",
            FontSize = 12,
            LineHeight = 16,
            Foreground = global::App.Controls.ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        _manualPanel.Children.Add(_secretText);
        _manualPanel.Children.Add(_copyButton);
        _manualPanel.Children.Add(_copyStatus);
        SetColumn(_manualPanel, 1);
        Children.Add(_manualPanel);
        SizeChanged += OnPanelSizeChanged;
        ActualThemeChanged += (_, _) => UpdateTheme();
        Loaded += (_, _) => UpdateTheme();
    }

    private void UpdateTheme()
    {
        if (_manualPanel.Children.FirstOrDefault() is TextBlock help)
            help.Foreground = global::App.Controls.ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush");
        _secretText.Foreground = global::App.Controls.ThemeBrushResolver.Resolve(this, "TextFillColorPrimaryBrush");
        _copyStatus.Foreground = global::App.Controls.ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush");
    }

    public static async Task<TotpSetupCredentialsPanel?> CreateAsync(
        string secret,
        string otpauthUri,
        VaultSessionLease expectedSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(otpauthUri);
        ArgumentNullException.ThrowIfNull(expectedSession);
        var panel = new TotpSetupCredentialsPanel(expectedSession);
        var registry = global::App.App.Services.GetRequiredService<ITransientOperationRegistry>();
        if (!panel.TryPublish(() =>
            {
                panel._registration = registry.Register(
                    panel.CancelPending,
                    panel.ClearSensitive,
                    panel.ClearSensitive);
                panel._secret = secret;
                panel._secretText.Text = FormatSecret(secret);
                panel._copyButton.IsEnabled = true;
            }))
        {
            panel.Dispose();
            return null;
        }

        try
        {
            var bitmap = await TotpQrCodeRenderer.RenderAsync(otpauthUri, panel._cancellation.Token);
            if (!panel.TryPublish(() => panel._qrImage.Source = bitmap))
            {
                panel.Dispose();
                return null;
            }
            return panel;
        }
        catch (OperationCanceledException) when (panel._cancellation.IsCancellationRequested)
        {
            panel.Dispose();
            return null;
        }
        catch (Exception)
        {
            // A failed local rendering must leave the manual setup path available.
            if (panel.TryPublish(() => panel._qrFallback.Visibility = Visibility.Visible))
                return panel;
            panel.Dispose();
            return null;
        }
    }

    private static string FormatSecret(string secret) => string.Join(
        " ",
        Enumerable.Range(0, (secret.Length + 3) / 4)
            .Select(index => secret.Substring(index * 4, Math.Min(4, secret.Length - index * 4))));

    private bool TryPublish(Action publish)
    {
        var published = false;
        _lifecycle.TryRunSensitive(() => _session.TryRunIfUnlocked(_expectedSession, () =>
        {
            if (Volatile.Read(ref _isCleared) != 0 || Volatile.Read(ref _isDisposed) != 0)
                return;
            publish();
            published = true;
        }));
        return published;
    }

    private void OnCopySecretClick(object sender, RoutedEventArgs e)
    {
        try
        {
            TryPublish(() =>
            {
                _clipboard.SetSecretText(_secret);
                _copyStatus.Text = "密钥已复制";
            });
        }
        catch (Exception)
        {
            TryPublish(() => _copyStatus.Text = "无法复制密钥，请重试");
        }
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var isStacked = e.NewSize.Width > 0 && e.NewSize.Width < 360;
        if (isStacked == _isStacked)
            return;
        _isStacked = isStacked;
        ColumnSpacing = isStacked ? 0 : 20;
        RowSpacing = isStacked ? 16 : 0;
        ColumnDefinitions[0].Width = isStacked ? new GridLength(1, GridUnitType.Star) : new GridLength(148);
        ColumnDefinitions[1].Width = isStacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SetColumn(_manualPanel, isStacked ? 0 : 1);
        SetRow(_manualPanel, isStacked ? 1 : 0);
    }

    public void ClearSensitive()
    {
        Interlocked.Exchange(ref _isCleared, 1);
        _secret = string.Empty;
        void ClearView()
        {
            _qrImage.Source = null;
            _qrFallback.Visibility = Visibility.Collapsed;
            _secretText.Text = string.Empty;
            _copyStatus.Text = string.Empty;
            _copyButton.IsEnabled = false;
        }

        if (DispatcherQueue.HasThreadAccess)
            ClearView();
        else
            DispatcherQueue.TryEnqueue(ClearView);
    }

    private void CancelPending()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;
        ClearSensitive();
        CancelPending();
        Interlocked.Exchange(ref _registration, null)?.Dispose();
        _cancellation.Dispose();
    }
}
