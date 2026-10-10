using App.Services;
using App.ViewModels.Models;
using App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.ViewManagement;

namespace App.Controls;

public sealed partial class FieldRow : UserControl
{
    private bool _revealed;
    private bool _revealBusy;
    private bool _copyBusy;
    private CancellationTokenSource? _operationCancellation;
    private long _viewVersion;
    private long _copiedVersion;
    private IClipboardStatus? _clipboardStatus;
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public FieldRow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ResetField();
        ActualThemeChanged += (_, _) => Render();
        Loaded += (_, _) =>
        {
            _clipboardStatus = global::App.App.Services?.GetService<IClipboardService>() as IClipboardStatus;
            if (_clipboardStatus is not null) _clipboardStatus.Changed += OnClipboardChanged;
            Render();
        };
        Unloaded += (_, _) =>
        {
            if (_clipboardStatus is not null) _clipboardStatus.Changed -= OnClipboardChanged;
            _clipboardStatus = null;
            ResetField();
        };
        _feedbackTimer.Tick += (_, _) => ClearFeedback();
    }

    public string CopyButtonText { get => (string)GetValue(CopyButtonTextProperty); set => SetValue(CopyButtonTextProperty, value); }
    public static readonly DependencyProperty CopyButtonTextProperty = RegisterString(nameof(CopyButtonText), string.Empty);
    public string EmptyValueText { get => (string)GetValue(EmptyValueTextProperty); set => SetValue(EmptyValueTextProperty, value); }
    public static readonly DependencyProperty EmptyValueTextProperty = RegisterString(nameof(EmptyValueText), "未设置");
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public static readonly DependencyProperty LabelProperty = RegisterString(nameof(Label), string.Empty);
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(FieldRow),
        new PropertyMetadata(string.Empty, (d, _) => ((FieldRow)d).ResetField()));
    public bool IsSecret { get => (bool)GetValue(IsSecretProperty); set => SetValue(IsSecretProperty, value); }
    public static readonly DependencyProperty IsSecretProperty = DependencyProperty.Register(nameof(IsSecret), typeof(bool), typeof(FieldRow),
        new PropertyMetadata(false, (d, _) => ((FieldRow)d).ResetField()));
    public bool ShowOpen { get => (bool)GetValue(ShowOpenProperty); set => SetValue(ShowOpenProperty, value); }
    public static readonly DependencyProperty ShowOpenProperty = RegisterBoolean(nameof(ShowOpen), false);
    public bool HideWhenEmpty { get => (bool)GetValue(HideWhenEmptyProperty); set => SetValue(HideWhenEmptyProperty, value); }
    public static readonly DependencyProperty HideWhenEmptyProperty = RegisterBoolean(nameof(HideWhenEmpty), true);
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public static readonly DependencyProperty IsReadOnlyProperty = RegisterBoolean(nameof(IsReadOnly), false);
    public bool IsMultiline { get => (bool)GetValue(IsMultilineProperty); set => SetValue(IsMultilineProperty, value); }
    public static readonly DependencyProperty IsMultilineProperty = RegisterBoolean(nameof(IsMultiline), false);
    public event EventHandler? FieldUsed;
    public Func<CancellationToken, Task<bool>>? AuthorizeItemAsync { get; set; }

    private static DependencyProperty RegisterString(string name, string value) => DependencyProperty.Register(
        name, typeof(string), typeof(FieldRow), new PropertyMetadata(value, (d, _) => ((FieldRow)d).Render()));
    private static DependencyProperty RegisterBoolean(string name, bool value) => DependencyProperty.Register(
        name, typeof(bool), typeof(FieldRow), new PropertyMetadata(value, (d, _) => ((FieldRow)d).Render()));

    private void ResetField()
    {
        _viewVersion++;
        _operationCancellation?.Cancel();
        _revealed = false;
        _copiedVersion = 0;
        ClearFeedback();
        Render();
    }

    private void Render()
    {
        if (ValueText is null)
            return;
        var empty = string.IsNullOrEmpty(Value);
        var readOnly = IsReadOnly || IsDeletedContext(this);
        var multiline = IsMultiline || Value?.Contains('\n') == true;
        var hasLabel = !string.IsNullOrWhiteSpace(Label);
        LabelColumn.Width = new GridLength(hasLabel ? 112 : 0);
        RowGrid.ColumnSpacing = hasLabel ? 16 : 0;
        RowBorder.BorderThickness = hasLabel ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        LabelText.Text = Label ?? string.Empty;
        ValueText.Inlines.Clear();
        ValueText.Text = empty ? EmptyValueText : IsSecret && !_revealed ? "••••••••••••" : Value;
        ValueText.FontFamily = IsSecret ? (FontFamily)Application.Current.Resources["MonospaceFontFamily"] : FontFamily;
        ValueText.CharacterSpacing = IsSecret ? (_revealed ? 20 : 140) : 0;
        ValueText.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ValueText.TextTrimming = multiline ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        ValueText.LineHeight = multiline ? 26 : 20;
        ValueText.MaxWidth = multiline ? hasLabel ? 560 : 600 : double.PositiveInfinity;
        ValueText.HorizontalAlignment = HorizontalAlignment.Left;
        RowGrid.Padding = new Thickness(0, multiline ? 14 : 8, 0, multiline ? 14 : 8);
        LabelText.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (ShowOpen && !empty && Uri.TryCreate(Value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            ValueText.Text = string.Empty;
            ValueText.Inlines.Add(new Run { Text = uri.IsDefaultPort ? uri.Host : uri.Authority });
            ValueText.Inlines.Add(new Run
            {
                Text = (uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery) + uri.Fragment,
                Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush"),
            });
        }
        RevealIcon.Glyph = _revealed ? "\uED1A" : "\uE890";
        RevealButton.Visibility = IsSecret && !readOnly ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.Visibility = ShowOpen && !readOnly ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        CopyButton.IsEnabled = RevealButton.IsEnabled = OpenButton.IsEnabled = !empty;
        CopyButton.IsEnabled &= !_copyBusy && !_revealBusy;
        RevealButton.IsEnabled &= !_revealBusy && !_copyBusy;
        var label = string.IsNullOrWhiteSpace(Label) ? (ShowOpen ? "网址" : "字段") : Label;
        AutomationProperties.SetName(CopyButton, $"复制{label}");
        AutomationProperties.SetName(RevealButton, $"{(_revealed ? "隐藏" : "显示")}{label}");
        AutomationProperties.SetName(OpenButton, $"在浏览器中打开{label}");
        ToolTipService.SetToolTip(CopyButton, $"复制{label}");
        ToolTipService.SetToolTip(RevealButton, _revealed ? "隐藏" : "显示（需 Windows Hello）");
        CopyLabel.Text = CopyButtonText ?? string.Empty;
        CopyLabel.Visibility = string.IsNullOrWhiteSpace(CopyButtonText) ? Visibility.Collapsed : Visibility.Visible;
        Visibility = HideWhenEmpty && empty ? Visibility.Collapsed : Visibility.Visible;
        UpdateCopyState();
    }

    internal static bool IsDeletedContext(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: CipherDetail detail }) return detail.IsDeleted;
        return false;
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
        var label = string.IsNullOrWhiteSpace(Label) ? (ShowOpen ? "网址" : "字段") : Label;
        AutomationProperties.SetName(CopyButton, copied ? $"已复制{label}" : $"复制{label}");
        ToolTipService.SetToolTip(CopyButton, copied ? $"已复制{label}" : $"复制{label}");
    }

    private async void OnReveal(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Value) || IsReadOnly || IsDeletedContext(this) || _revealBusy || _copyBusy) return;
        if (_revealed)
        {
            _revealed = false;
            Render();
            return;
        }
        var version = _viewVersion;
        var value = Value;
        using var cancellation = new CancellationTokenSource();
        var operationToken = cancellation.Token;
        _operationCancellation = cancellation;
        _revealBusy = true;
        Render();
        try
        {
            if (!await AuthorizeCurrentItemAsync(version, value, operationToken)) return;
            var itemName = (DataContext as CipherDetail)?.Name;
            if (!await VerifyRevealAsync(this, Label, itemName, operationToken)
                || !await AuthorizeCurrentItemAsync(version, value, operationToken)) return;
            var lifecycle = global::App.App.Services?.GetService<IAppLifecycleCoordinator>();
            if (lifecycle?.TryRunSensitive(() =>
            {
                if (!IsCurrentOperation(version, value, operationToken)) return false;
                _revealed = true;
                return true;
            }) != true) return;
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { }
        catch (Exception exception) when (Api.RequestTimeoutClassifier.IsTimeout(exception))
        {
            ShowFeedback(UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or OperationCanceledException)
        {
            ShowFeedback("无法完成验证，请重试。");
        }
        finally
        {
            _operationCancellation = null;
            _revealBusy = false;
            Render();
        }
    }

    internal static async Task<bool> VerifyRevealAsync(UserControl owner, string label, string? itemName, CancellationToken ct = default)
    {
        var verification = global::App.App.Services?.GetService<ISensitiveActionVerificationService>();
        var window = global::App.App.MainWindow;
        if (verification is null || window is null || owner.XamlRoot is null) return false;
        var message = string.IsNullOrWhiteSpace(itemName)
            ? $"WinVaultWarden 想要显示{label}。"
            : $"WinVaultWarden 想要显示“{itemName}”的{label}。";
        return await verification.VerifyAsync(WinRT.Interop.WindowNative.GetWindowHandle(window), message, async cancellationToken =>
        {
            var input = new PasswordBox { PlaceholderText = "主密码", MinWidth = 300 };
            var dialog = new ContentDialog
            {
                Title = "确认是你本人",
                Content = input,
                PrimaryButtonText = "验证",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = owner.XamlRoot,
            };
            try
            {
                var result = await DialogHelper.ShowTrackedAsync(dialog, () => input.Password = string.Empty, cancellationToken);
                return result == ContentDialogResult.Primary ? input.Password : null;
            }
            finally { input.Password = string.Empty; }
        }, ct);
    }

    private bool IsCurrentOperation(long version, string? value, CancellationToken ct) =>
        !ct.IsCancellationRequested && IsLoaded && version == _viewVersion
        && !IsReadOnly && !IsDeletedContext(this) && !string.IsNullOrEmpty(Value)
        && string.Equals(Value, value, StringComparison.Ordinal);

    private async Task<bool> AuthorizeCurrentItemAsync(long version, string? value, CancellationToken ct)
    {
        if (!IsCurrentOperation(version, value, ct)) return false;
        var authorize = AuthorizeItemAsync;
        if (authorize is not null && !await authorize(ct).WaitAsync(ct)) return false;
        return IsCurrentOperation(version, value, ct);
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Value) || IsReadOnly || IsDeletedContext(this) || _copyBusy || _revealBusy) return;
        var version = _viewVersion;
        var value = Value;
        using var cancellation = new CancellationTokenSource();
        var operationToken = cancellation.Token;
        _operationCancellation = cancellation;
        _copyBusy = true;
        Render();
        var clipboard = global::App.App.Services?.GetService<IClipboardService>();
        var lifecycle = global::App.App.Services?.GetService<IAppLifecycleCoordinator>();
        try
        {
            if (!await AuthorizeCurrentItemAsync(version, value, operationToken)) return;
            if (clipboard is null || lifecycle?.TryRunSensitive(() =>
            {
                if (!IsCurrentOperation(version, value, operationToken)) return false;
                clipboard.SetText(Value);
                (clipboard as IClipboardStatus)?.SetLabel(string.IsNullOrWhiteSpace(Label) ? "内容" : Label);
                return true;
            }) != true) return;
            _copiedVersion = _clipboardStatus?.WriteVersion ?? 0;
            UpdateCopyState();
            ClearFeedback();
            FieldUsed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { }
        catch (Exception exception) when (Api.RequestTimeoutClassifier.IsTimeout(exception))
        {
            ShowFeedback(UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or OperationCanceledException)
        {
            ShowFeedback("复制失败，请重试。");
        }
        finally
        {
            _operationCancellation = null;
            _copyBusy = false;
            Render();
        }
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (IsReadOnly || IsDeletedContext(this) || !Uri.TryCreate(Value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
        var version = _viewVersion;
        try
        {
            if (await Launcher.LaunchUriAsync(uri) && version == _viewVersion && IsLoaded)
                FieldUsed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception) { ShowFeedback("无法打开链接。"); }
    }
}

/// <summary>Resolves imperative brushes against the element's theme instead of the application's default theme.</summary>
public static class ThemeBrushResolver
{
    public static Brush Resolve(FrameworkElement owner, string key)
    {
        var dark = owner.ActualTheme == ElementTheme.Dark;
        var highContrast = new AccessibilitySettings().HighContrast;
        var themeName = highContrast ? "HighContrast" : dark ? "Dark" : "Light";
        for (DependencyObject? node = owner; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element && FindThemedBrush(element.Resources, themeName, key) is { } local)
                return local;
        }
        if (FindThemedBrush(Application.Current.Resources, themeName, key) is { } application)
            return application;

        if (highContrast)
        {
            var settings = new UISettings();
            var elementColor = key switch
            {
                "AccentTextFillColorPrimaryBrush" or "AccentTextFillColorSecondaryBrush"
                    or "AccentTextFillColorTertiaryBrush" => UIElementType.Hotlight,
                "TextOnAccentFillColorPrimaryBrush" or "TextOnAccentFillColorSecondaryBrush"
                    or "TextOnAccentFillColorDisabledBrush" => UIElementType.HighlightText,
                "AccentFillColorDefaultBrush" or "AccentFillColorSecondaryBrush"
                    or "AccentFillColorTertiaryBrush" => UIElementType.Highlight,
                "SubtleFillColorSecondaryBrush" or "SubtleFillColorTertiaryBrush"
                    or "SubtleFillColorSelectedBrush" => UIElementType.Highlight,
                "ControlFillColorDefaultBrush" or "ControlFillColorSecondaryBrush"
                    or "ControlFillColorTertiaryBrush" or "CardBackgroundFillColorDefaultBrush"
                    or "SolidBackgroundFillColorBaseBrush" or "TileFillColorBrush"
                    or "SubtleFillColorTransparentBrush" => UIElementType.Window,
                "TextFillColorDisabledBrush" => UIElementType.GrayText,
                _ => UIElementType.WindowText,
            };
            return new SolidColorBrush(settings.UIElementColor(elementColor));
        }

        if (key is "SubtleFillColorTransparentBrush" or "SubtleFillColorSecondaryBrush"
            or "SubtleFillColorTertiaryBrush" or "SubtleFillColorSelectedBrush")
        {
            var alpha = key switch
            {
                "SubtleFillColorTransparentBrush" => 0,
                "SubtleFillColorSecondaryBrush" => dark ? .045 : .034,
                _ => dark ? .07 : .058,
            };
            var channel = dark ? (byte)255 : (byte)0;
            return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb((byte)Math.Round(alpha * 255), channel, channel, channel));
        }

        // Native system resources can be deferred outside accessible theme dictionaries.
        // Keep explicit window themes legible with the handoff's semantic color pairs.
        var color = key switch
        {
            "TextFillColorPrimaryBrush" => dark ? 0xFFFFFFu : 0x1B1B1Bu,
            "TextFillColorSecondaryBrush" => dark ? 0xC8C8C8u : 0x555555u,
            "TextFillColorTertiaryBrush" => dark ? 0xA0A0A0u : 0x6B6B6Bu,
            "TextFillColorDisabledBrush" => dark ? 0x888888u : 0x888888u,
            "SystemFillColorSuccessBrush" => dark ? 0x6CCB5Fu : 0x0F7B0Fu,
            "SystemFillColorCautionBrush" => dark ? 0xFCE100u : 0x9D5D00u,
            "SystemFillColorCriticalBrush" => dark ? 0xFF99A4u : 0xC42B1Cu,
            "DividerStrokeColorDefaultBrush" => dark ? 0x333333u : 0xEBEBEBu,
            "CardStrokeColorDefaultBrush" => dark ? 0x3B3B3Bu : 0xE0E0E0u,
            "ControlFillColorDefaultBrush" => dark ? 0x2D2D2Du : 0xFFFFFFu,
            "ControlFillColorSecondaryBrush" or "ControlFillColorTertiaryBrush"
                or "TileFillColorBrush" => dark ? 0x363636u : 0xEFEFEFu,
            "CardBackgroundFillColorDefaultBrush" => dark ? 0x282828u : 0xFFFFFFu,
            "SolidBackgroundFillColorBaseBrush" => dark ? 0x202020u : 0xF3F3F3u,
            _ => dark ? 0xFFFFFFu : 0x1B1B1Bu,
        };
        if (key.StartsWith("Accent", StringComparison.Ordinal))
        {
            try
            {
                var settings = new UISettings();
                return new SolidColorBrush(settings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1));
            }
            catch (Exception)
            {
                color = dark ? 0x60CDFFu : 0x005FB8u;
            }
        }
        return new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255,
            (byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }

    private static Brush? FindThemedBrush(ResourceDictionary dictionary, string themeName, string key)
    {
        if (dictionary.ThemeDictionaries.ContainsKey(themeName)
            && dictionary.ThemeDictionaries[themeName] is ResourceDictionary themed)
        {
            // Enumeration avoids a resource lookup falling through to the global default theme.
            foreach (var resource in themed)
                if (Equals(resource.Key, key) && resource.Value is Brush brush)
                    return brush;
        }
        for (var index = dictionary.MergedDictionaries.Count - 1; index >= 0; index--)
            if (FindThemedBrush(dictionary.MergedDictionaries[index], themeName, key) is { } merged)
                return merged;
        return null;
    }
}
