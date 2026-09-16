using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Extensions.DependencyInjection;
using App.Services;
using System.Runtime.InteropServices;

namespace App.Controls;

public sealed partial class TotpField : UserControl
{
    private const string Placeholder = "— — —";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // 当前有效的 6 位验证码(由 Secret 计算);无法解析时为 null(此时禁用复制、显示占位)。
    private string? _currentCode;

    public TotpField()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        _feedbackTimer.Tick += (_, _) => ClearFeedback();
        Loaded += (_, _) => { Tick(); _timer.Start(); };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            ClearFeedback();
            ClearCode();
        };
        DataContextChanged += (_, _) =>
        {
            ClearFeedback();
            ClearCode();
            // Allow inherited bindings to update before reading a reused template's secret.
            if (IsLoaded)
                DispatcherQueue.TryEnqueue(Tick);
        };
        ClearCode();
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
            new PropertyMetadata("", (d, _) => ((TotpField)d).OnSecretChanged()));

    private void OnSecretChanged()
    {
        ClearFeedback();
        Tick();
    }

    private static string Format(string code) =>
        code is { Length: 6 } ? $"{code[..3]} {code[3..]}" : code;

    private void Tick()
    {
        // Unloaded cached pages must not retain newly generated codes after binding updates.
        if (!IsLoaded)
        {
            ClearCode();
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        UpdateCode(now);
    }

    private void UpdateCode(long unixSeconds)
    {
        if (Crypto.TotpGenerator.TryGenerate(Secret, unixSeconds, out var code))
        {
            var period = GetPeriod(Secret);
            var remaining = (int)(period - (unixSeconds % period));
            Ring.Maximum = period;
            Ring.Value = remaining;
            Ring.Visibility = Visibility.Visible;
            SecondsText.Text = remaining.ToString();
            _currentCode = code;
            CodeText.Text = Format(code);
            CopyButton.IsEnabled = true;
        }
        else
        {
            ClearCode();
        }
    }

    private static int GetPeriod(string? secret)
    {
        // Match the generator's default and optional otpauth period without retaining the seed.
        var period = 30;
        if (string.IsNullOrWhiteSpace(secret)
            || !secret.Trim().StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(secret.Trim(), UriKind.Absolute, out var uri))
            return period;

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && Uri.UnescapeDataString(pair[0]).Equals("period", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(Uri.UnescapeDataString(pair[1]), out var parsedPeriod)
                && parsedPeriod > 0)
                period = parsedPeriod;
        }

        return period;
    }

    private void ClearCode()
    {
        _currentCode = null;
        CodeText.Text = Placeholder;
        CopyButton.IsEnabled = false;
        Ring.Value = 0;
        Ring.Visibility = Visibility.Collapsed;
        SecondsText.Text = string.Empty;
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

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        var clipboard = global::App.App.Services?.GetService<global::App.Services.IClipboardService>();
        var lifecycle = global::App.App.Services?.GetService<IAppLifecycleCoordinator>();
        try
        {
            bool Write()
            {
                // Refresh inside the lifecycle gate so a delayed UI tick cannot copy an expired code.
                Tick();
                if (_currentCode is not { } code)
                    return false;
                if (clipboard is not null) clipboard.SetSecretText(code);
                else
                {
                    var package = new DataPackage();
                    package.SetText(code);
                    if (!Clipboard.SetContentWithOptions(package, new ClipboardContentOptions
                    {
                        IsAllowedInHistory = false,
                        IsRoamable = false,
                    })) throw new InvalidOperationException("The protected clipboard write was not accepted.");
                }
                return true;
            }
            if (lifecycle is not null ? lifecycle.TryRunSensitive(Write) : Write())
                ShowFeedback("已复制验证码");
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            ShowFeedback("复制失败，请重试。");
        }
    }
}
