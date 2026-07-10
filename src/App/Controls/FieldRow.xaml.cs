using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.Extensions.DependencyInjection;

namespace App.Controls;

public sealed partial class FieldRow : UserControl
{
    private const string GlyphView = "";  // View
    private const string GlyphHide = "";  // Hide

    private bool _revealed;

    public FieldRow() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(FieldRow),
            new PropertyMetadata("", (d, e) => ((FieldRow)d).LabelText.Text = (string)e.NewValue));

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
        if (IsSecret && _revealed)
            SetRevealed(false);
        Render();
    }

    // 统一设置揭示状态与揭示按钮的图标/无障碍名/提示,保证三者始终一致。
    private void SetRevealed(bool revealed)
    {
        _revealed = revealed;
        RevealIcon.Glyph = revealed ? GlyphHide : GlyphView;
        var label = revealed ? "隐藏" : "显示";
        AutomationProperties.SetName(RevealButton, label);
        ToolTipService.SetToolTip(RevealButton, label);
    }

    private void Render()
    {
        ValueText.Text = IsSecret && !_revealed ? new string('•', 8) : Value;
        UpdateRowVisibility();
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
        // 条目级门禁已在详情层完成(未授权时 Value 本身即脱敏),FieldRow 只负责遮蔽/揭示已授权的 Value。
        SetRevealed(!_revealed);
        Render();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var value = Value ?? string.Empty;
        var clipboard = global::App.App.Services?.GetService<global::App.Services.IClipboardService>();
        if (clipboard is null)
        {
            // 回退:无 DI(设计时)时直接写,保证不崩。
            var dp = new DataPackage();
            dp.SetText(value);
            Clipboard.SetContent(dp);
            return;
        }

        if (IsSecret)
            clipboard.SetSecretText(value);
        else
            clipboard.SetText(value);
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
