using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Extensions.DependencyInjection;

namespace App.Controls;

public sealed partial class TotpField : UserControl
{
    private const string Placeholder = "— — —";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    // 当前有效的 6 位验证码(由 Secret 计算);无法解析时为 null(此时禁用复制、显示占位)。
    private string? _currentCode;

    public TotpField()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { Tick(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// 输入的 TOTP 密钥:Bitwarden 的 Totp 字段(裸 Base32 或 otpauth:// URI)。
    /// 本控件据此计算并显示**动态验证码**——绝不直接显示/复制原始密钥(S9)。
    /// </summary>
    public string Secret
    {
        get => (string)GetValue(SecretProperty);
        set => SetValue(SecretProperty, value);
    }
    public static readonly DependencyProperty SecretProperty =
        DependencyProperty.Register(nameof(Secret), typeof(string), typeof(TotpField),
            new PropertyMetadata("", (d, e) => ((TotpField)d).Tick()));

    private static string Format(string code) =>
        code is { Length: 6 } ? $"{code[..3]} {code[3..]}" : code;

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remaining = (int)(30 - (now % 30));
        Ring.Value = remaining;
        SecondsText.Text = remaining.ToString();
        UpdateCode(now);
    }

    private void UpdateCode(long unixSeconds)
    {
        if (Crypto.TotpGenerator.TryGenerate(Secret, unixSeconds, out var code))
        {
            _currentCode = code;
            CodeText.Text = Format(code);
            CopyButton.IsEnabled = true;
        }
        else
        {
            // 密钥缺失或无法解析:显示占位并禁用复制,绝不把原始种子当验证码呈现/复制。
            _currentCode = null;
            CodeText.Text = Placeholder;
            CopyButton.IsEnabled = false;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_currentCode is not { } code)
            return;

        var clipboard = global::App.App.Services?.GetService<global::App.Services.IClipboardService>();
        if (clipboard is null)
        {
            var dp = new DataPackage();
            dp.SetText(code);
            Clipboard.SetContent(dp);
            return;
        }

        clipboard.SetSecretText(code);
    }
}
