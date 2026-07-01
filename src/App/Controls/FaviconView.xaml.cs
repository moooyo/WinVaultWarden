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
        _cts?.Cancel();
        IconImage.Visibility = Visibility.Collapsed;   // 默认显字形
        IconImage.Source = null;

        var domain = Domain;
        if (!global::App.Services.AppPreferences.Current.ShowWebsiteIcons || string.IsNullOrEmpty(domain))
            return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var cache = global::App.App.Services.GetRequiredService<IFaviconCache>();
            var bytes = await cache.GetAsync(domain, cts.Token);
            if (cts.Token.IsCancellationRequested || bytes is null || bytes.Length == 0)
                return;
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            await bmp.SetSourceAsync(ms.AsRandomAccessStream());
            if (cts.Token.IsCancellationRequested) return;
            IconImage.Source = bmp;
            IconImage.Visibility = Visibility.Visible;  // 盖住字形
        }
        catch { /* 任何失败 → 保持字形 */ }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e) => IconImage.Visibility = Visibility.Visible;
    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e) => IconImage.Visibility = Visibility.Collapsed;
}
