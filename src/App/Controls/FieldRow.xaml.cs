using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;
using App.Services;
using System.Runtime.InteropServices;

namespace App.Controls;

public sealed partial class FieldRow : UserControl
{
    private const string GlyphView = "";  // View
    private const string GlyphHide = "";  // Hide

    private bool _revealed;

    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public FieldRow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { SetRevealed(false); ClearFeedback(); Render(); };
        Unloaded += (_, _) => { SetRevealed(false); ClearFeedback(); Render(); };
        _feedbackTimer.Tick += (_, _) => ClearFeedback();
    }

    public string CopyButtonText
    {
        get => (string)GetValue(CopyButtonTextProperty);
        set => SetValue(CopyButtonTextProperty, value);
    }
    public static readonly DependencyProperty CopyButtonTextProperty =
        DependencyProperty.Register(nameof(CopyButtonText), typeof(string), typeof(FieldRow),
            new PropertyMetadata(string.Empty, (d, _) => ((FieldRow)d).UpdateCopyPresentation()));

    public string EmptyValueText
    {
        get => (string)GetValue(EmptyValueTextProperty);
        set => SetValue(EmptyValueTextProperty, value);
    }
    public static readonly DependencyProperty EmptyValueTextProperty =
        DependencyProperty.Register(nameof(EmptyValueText), typeof(string), typeof(FieldRow),
            new PropertyMetadata("未设置", (d, _) => ((FieldRow)d).Render()));

    private void UpdateCopyPresentation()
    {
        var labelled = !string.IsNullOrWhiteSpace(CopyButtonText);
        CopyLabel.Text = CopyButtonText ?? string.Empty;
        CopyLabel.Visibility = labelled ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Style = (Style)Resources[labelled ? "FieldRowTextCopyButtonStyle" : "FieldRowIconCopyButtonStyle"];
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(FieldRow),
            new PropertyMetadata("", (d, e) => ((FieldRow)d).OnLabelChanged((string)e.NewValue)));

    private void OnLabelChanged(string label)
    {
        LabelText.Text = label;
        UpdateAutomationNames();
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(FieldRow),
            new PropertyMetadata("", (d, e) => ((FieldRow)d).OnValueChanged()));

    public bool IsSecret
    {
        get => (bool)GetValue(IsSecretProperty);
        set => SetValue(IsSecretProperty, value);
    }
    public static readonly DependencyProperty IsSecretProperty =
        DependencyProperty.Register(nameof(IsSecret), typeof(bool), typeof(FieldRow),
            new PropertyMetadata(false, (d, e) => ((FieldRow)d).OnSecretChanged()));

    public bool ShowOpen
    {
        get => (bool)GetValue(ShowOpenProperty);
        set => SetValue(ShowOpenProperty, value);
    }
    public static readonly DependencyProperty ShowOpenProperty =
        DependencyProperty.Register(nameof(ShowOpen), typeof(bool), typeof(FieldRow),
            new PropertyMetadata(false, (d, e) =>
                ((FieldRow)d).OpenButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public bool HideWhenEmpty
    {
        get => (bool)GetValue(HideWhenEmptyProperty);
        set => SetValue(HideWhenEmptyProperty, value);
    }
    public static readonly DependencyProperty HideWhenEmptyProperty =
        DependencyProperty.Register(nameof(HideWhenEmpty), typeof(bool), typeof(FieldRow),
            new PropertyMetadata(true, (d, e) => ((FieldRow)d).UpdateRowVisibility()));

    private void OnSecretChanged()
    {
        RevealButton.Visibility = IsSecret ? Visibility.Visible : Visibility.Collapsed;
        SetRevealed(false);
        Render();
    }

    // 值变更 = 切换到(可能不同的)条目字段。若此前点过"显示",必须复位为遮蔽——
    // 否则同类型条目间切换时(FieldRow 可视树被复用、IsSecret 恒为 True 不再触发 OnSecretChanged),
    // 新条目的密码/卡号/私钥会未经点击直接以明文渲染(S3:跨条目机密泄露)。
    private void OnValueChanged()
    {
        ClearFeedback();
        if (IsSecret && _revealed)
            SetRevealed(false);
        Render();
    }

    // 统一设置揭示状态与揭示按钮的图标/无障碍名/提示,保证三者始终一致。
    private void SetRevealed(bool revealed)
    {
        _revealed = revealed;
        RevealIcon.Glyph = revealed ? GlyphHide : GlyphView;
        ToolTipService.SetToolTip(RevealButton, revealed ? "隐藏" : "显示");
        UpdateAutomationNames();
    }

    // 字段级操作按钮的无障碍名包含字段标签,便于 Narrator 逐条区分(如"密码，复制"/"密码，显示")。
    private void UpdateAutomationNames()
    {
        var label = string.IsNullOrWhiteSpace(Label) ? "字段" : Label;
        AutomationProperties.SetName(CopyButton, $"{label}，复制");
        AutomationProperties.SetName(
            RevealButton,
            $"{label}，{(_revealed ? "隐藏" : "显示")}");
        AutomationProperties.SetName(OpenButton, $"{label}，打开");
    }

    private void Render()
    {
        var empty = string.IsNullOrEmpty(Value);
        ValueText.Text = empty ? EmptyValueText : IsSecret && !_revealed ? new string('•', 8) : Value;
        CopyButton.IsEnabled = !empty;
        RevealButton.IsEnabled = !empty;
        OpenButton.IsEnabled = !empty;
        UpdateRowVisibility();
    }

    private void ClearFeedback()
    {
        _feedbackTimer.Stop();
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

    // HideWhenEmpty=true 且 Value 为空 → 整个 FieldRow 折叠。
    private void UpdateRowVisibility()
    {
        Visibility = HideWhenEmpty && string.IsNullOrEmpty(Value)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // 揭示/复制不再自持门禁:条目级门禁在详情层完成(未授权时 Value 本身已脱敏),
    // FieldRow 只负责遮蔽/揭示与复制已授权的 Value。
    private void OnReveal(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Value)) return;
        // 条目级门禁已在详情层完成(未授权时 Value 本身即脱敏),FieldRow 只负责遮蔽/揭示已授权的 Value。
        SetRevealed(!_revealed);
        Render();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var value = Value ?? string.Empty;
        if (value.Length == 0) return;
        var clipboard = global::App.App.Services?.GetService<global::App.Services.IClipboardService>();
        var lifecycle = global::App.App.Services?.GetService<IAppLifecycleCoordinator>();
        try
        {
            bool Write()
            {
                if (clipboard is null) SetProtectedClipboardText(value);
                else clipboard.SetText(value);
                return true;
            }
            if (lifecycle is not null ? lifecycle.TryRunSensitive(Write) : Write())
                ShowFeedback($"已复制{Label}");
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            ShowFeedback("复制失败，请重试。");
        }
    }

    private static void SetProtectedClipboardText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        if (!Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        }))
            throw new InvalidOperationException("The protected clipboard write was not accepted.");
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(Value, UriKind.Absolute, out var uri))
            return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // 无默认处理器/受限协议等:忽略,不让 async void 异常崩溃 UI。
        }
    }
}
