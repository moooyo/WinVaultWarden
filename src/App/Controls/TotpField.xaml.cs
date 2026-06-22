using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace App.Controls;

public sealed partial class TotpField : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public TotpField()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => { Tick(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }
    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(nameof(Code), typeof(string), typeof(TotpField),
            new PropertyMetadata("000000", (d, e) => ((TotpField)d).CodeText.Text = Format((string)e.NewValue)));

    private static string Format(string? code) =>
        code is { Length: 6 } ? $"{code[..3]} {code[3..]}" : code ?? "";

    private void Tick()
    {
        // mock:用本地时钟算 30s 窗口剩余,真实 TOTP 后续接入。
        var secondsInWindow = DateTime.Now.Second % 30;
        var remaining = 30 - secondsInWindow;
        Ring.Value = remaining;
        SecondsText.Text = remaining.ToString();
        CodeText.Text = Format(Code);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(Code ?? string.Empty);
        Clipboard.SetContent(dp);
    }
}
