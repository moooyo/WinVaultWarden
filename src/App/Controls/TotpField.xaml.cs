using App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace App.Controls;

public sealed partial class TotpField : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? _currentCode;
    private IClipboardStatus? _clipboardStatus;
    private long _copiedVersion;

    public TotpField()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        _feedbackTimer.Tick += (_, _) => ClearFeedback();
        Loaded += (_, _) =>
        {
            _clipboardStatus = global::App.App.Services?.GetService<IClipboardService>() as IClipboardStatus;
            if (_clipboardStatus is not null) _clipboardStatus.Changed += OnClipboardChanged;
            UpdateLayoutMode();
            Tick();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            if (_clipboardStatus is not null) _clipboardStatus.Changed -= OnClipboardChanged;
            _clipboardStatus = null;
            ClearFeedback();
            ClearCode();
        };
        DataContextChanged += (_, _) =>
        {
            ClearFeedback();
            ClearCode();
            if (IsLoaded) DispatcherQueue.TryEnqueue(Tick);
        };
        ActualThemeChanged += (_, _) => { Tick(); UpdateCopyState(); };
        ClearCode();
    }

    /// <summary>Accepts a Bitwarden Base32 or otpauth seed and renders only its generated code.</summary>
    public string Secret { get => (string)GetValue(SecretProperty); set => SetValue(SecretProperty, value); }
    public static readonly DependencyProperty SecretProperty = DependencyProperty.Register(
        nameof(Secret), typeof(string), typeof(TotpField), new PropertyMetadata(string.Empty, (d, _) =>
        {
            var field = (TotpField)d;
            field._copiedVersion = 0;
            field.ClearFeedback();
            field.Tick();
        }));
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(TotpField), new PropertyMetadata(false, (d, _) => ((TotpField)d).UpdateLayoutMode()));
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(TotpField), new PropertyMetadata(false, (d, _) => ((TotpField)d).UpdateLayoutMode()));
    public bool ShowCountdown { get => (bool)GetValue(ShowCountdownProperty); set => SetValue(ShowCountdownProperty, value); }
    public static readonly DependencyProperty ShowCountdownProperty = DependencyProperty.Register(
        nameof(ShowCountdown), typeof(bool), typeof(TotpField), new PropertyMetadata(true, (d, _) => ((TotpField)d).Tick()));

    private void UpdateLayoutMode()
    {
        if (RowGrid is null) return;
        RowBorder.BorderThickness = IsCompact ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        RowGrid.Padding = IsCompact ? new Thickness(0) : new Thickness(0, 8, 0, 8);
        RowGrid.MinHeight = IsCompact ? 32 : 48;
        RowGrid.ColumnSpacing = IsCompact ? 0 : 16;
        LabelColumn.Width = new GridLength(IsCompact ? 0 : 112);
        LabelText.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.Visibility = IsCompact || IsReadOnly || FieldRow.IsDeletedContext(this) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Tick()
    {
        if (!IsLoaded) { ClearCode(); return; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!Crypto.TotpGenerator.TryGenerate(Secret, now, out var code)) { ClearCode(); return; }
        var period = GetPeriod(Secret);
        var remaining = (int)(period - now % period);
        Ring.Maximum = period;
        Ring.Value = remaining;
        Ring.Foreground = ThemeBrushResolver.Resolve(this, remaining <= 5 ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        CountdownPanel.Visibility = ShowCountdown ? Visibility.Visible : Visibility.Collapsed;
        SecondsText.Text = remaining.ToString();
        _currentCode = code;
        CodeText.Text = code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
        CopyButton.IsEnabled = true;
        ToolTipService.SetToolTip(CountdownPanel, $"验证码每 {period} 秒刷新");
        UpdateCopyState();
    }

    internal static int GetPeriod(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || !secret.Trim().StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(secret.Trim(), UriKind.Absolute, out var uri)) return 30;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]).Equals("period", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(Uri.UnescapeDataString(pair[1]), out var period) && period > 0) return period;
        }
        return 30;
    }

    private void ClearCode()
    {
        _currentCode = null;
        if (CodeText is null) return;
        CodeText.Text = "— — —";
        CopyButton.IsEnabled = false;
        Ring.Value = 0;
        CountdownPanel.Visibility = Visibility.Collapsed;
        SecondsText.Text = string.Empty;
    }

    private void ClearFeedback()
    {
        _feedbackTimer.Stop();
        if (CopyFeedback is null) return;
        CopyFeedback.Text = string.Empty;
        CopyFeedback.Visibility = Visibility.Collapsed;
    }

    private void ShowFeedback(string text)
    {
        CopyFeedback.Text = text;
        CopyFeedback.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void OnClipboardChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateCopyState);
    private void UpdateCopyState()
    {
        var copied = _clipboardStatus is { HasContent: true } && _copiedVersion != 0 && _copiedVersion == _clipboardStatus.WriteVersion;
        CopyIcon.Glyph = copied ? "\uE73E" : "\uE8C8";
        CopyIcon.Foreground = ThemeBrushResolver.Resolve(this, copied ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(CopyButton, copied ? "已复制验证码" : "复制验证码");
        ToolTipService.SetToolTip(CopyButton, copied ? "已复制验证码" : "复制验证码");
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || IsReadOnly || FieldRow.IsDeletedContext(this)) return;
        var clipboard = global::App.App.Services?.GetService<IClipboardService>();
        var lifecycle = global::App.App.Services?.GetService<IAppLifecycleCoordinator>();
        try
        {
            if (clipboard is null || lifecycle?.TryRunSensitive(() =>
            {
                Tick();
                if (_currentCode is not { } code) return false;
                clipboard.SetSecretText(code);
                (clipboard as IClipboardStatus)?.SetLabel("验证码");
                return true;
            }) != true) return;
            _copiedVersion = _clipboardStatus?.WriteVersion ?? 0;
            UpdateCopyState();
            ClearFeedback();
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            ShowFeedback("复制失败，请重试。");
        }
    }
}
