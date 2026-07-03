using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace App.Controls;

public sealed partial class FaviconView : UserControl
{
    private CancellationTokenSource? _cts;

    public FaviconView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        // 卸载时取消并释放最后一个 CTS,避免虚拟化长列表滚动持续泄漏 CancellationTokenSource(R11)。
        Unloaded += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    public string? Domain
    {
        get => (string?)GetValue(DomainProperty);
        set => SetValue(DomainProperty, value);
    }
    public static readonly DependencyProperty DomainProperty =
        DependencyProperty.Register(nameof(Domain), typeof(string), typeof(FaviconView),
            new PropertyMetadata(null, (d, _) => ((FaviconView)d).Reload()));

    public string FallbackGlyph
    {
        get => (string)GetValue(FallbackGlyphProperty);
        set => SetValue(FallbackGlyphProperty, value);
    }
    public static readonly DependencyProperty FallbackGlyphProperty =
        DependencyProperty.Register(nameof(FallbackGlyph), typeof(string), typeof(FaviconView),
            new PropertyMetadata("", (d, e) => ((FaviconView)d).GlyphIcon.Glyph = (string)(e.NewValue ?? "")));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(FaviconView),
            new PropertyMetadata(16.0, (d, e) => ((FaviconView)d).GlyphIcon.FontSize = (double)e.NewValue));

    private async void Reload()
    {
        // 取消并释放上一个 CTS(此前只 Cancel 不 Dispose,虚拟化列表滚动持续泄漏,R11)。
        var old = _cts;
        old?.Cancel();
        old?.Dispose();
        _cts = null;

        IconImage.Visibility = Visibility.Collapsed;   // 默认显字形
        IconImage.Source = null;

        var domain = Domain;
        if (!global::App.Services.AppPreferences.Current.ShowWebsiteIcons || string.IsNullOrEmpty(domain))
            return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        // 捕获 token(struct):后续 Reload 会把本 cts 当作 old 释放,而已捕获的 token 读
        // IsCancellationRequested 不受源释放影响,避免读已释放 CTS 抛异常。
        var token = cts.Token;
        try
        {
            var cache = global::App.App.Services.GetRequiredService<IFaviconCache>();
            var bytes = await cache.GetAsync(domain, token);
            if (token.IsCancellationRequested || bytes is null || bytes.Length == 0)
                return;
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            if (token.IsCancellationRequested) return;
            IconImage.Source = bmp;
            IconImage.Visibility = Visibility.Visible;  // 盖住字形
        }
        catch { /* 任何失败 → 保持字形 */ }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => IconImage.Visibility = Visibility.Visible;
    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e) => IconImage.Visibility = Visibility.Collapsed;
}
