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

    // 该字段所属条目是否标记了"主密码重提示"。为 true 且是机密字段时,首次揭示/复制前需验主密码。
    public bool RepromptRequired
    {
        get => (bool)GetValue(RepromptRequiredProperty);
        set => SetValue(RepromptRequiredProperty, value);
    }
    public static readonly DependencyProperty RepromptRequiredProperty =
        DependencyProperty.Register(nameof(RepromptRequired), typeof(bool), typeof(FieldRow),
            new PropertyMetadata(false));

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

    // 机密字段 + RepromptRequired + 本会话未验证时,弹主密码对话框验证;通过返回 true。
    // 复用 App.App.Services 服务定位(与下方 OnCopy 解析 IClipboardService 同一模式)。
    private async Task<bool> PassesRepromptAsync()
    {
        if (!IsSecret || !RepromptRequired)
            return true;
        var gate = global::App.App.Services?.GetService<global::App.Services.IMasterPasswordReprompt>();
        if (gate is null || gate.IsSessionVerified)
            return true;

        var passwordBox = new PasswordBox { PlaceholderText = "主密码", MinWidth = 320 };
        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "该条目要求主密码重提示", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(passwordBox);
        panel.Children.Add(errorText);
        var dialog = new ContentDialog
        {
            Title = "验证主密码",
            Content = panel,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;
            if (await gate.VerifyAsync(passwordBox.Password))
                return true;
            errorText.Text = "主密码不正确";
            errorText.Visibility = Visibility.Visible;
        }
    }

    private async void OnReveal(object sender, RoutedEventArgs e)
    {
        if (!_revealed && !await PassesRepromptAsync())
            return; // 仅揭示(遮蔽→明文)需验证;隐藏(明文→遮蔽)无需
        SetRevealed(!_revealed);
        Render();
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!await PassesRepromptAsync())
            return;
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
