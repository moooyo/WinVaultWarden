using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using App.Services;
using App.ViewModels.Models;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace App.Controls;

public sealed partial class FaviconView : UserControl
{
    private CancellationTokenSource? _cts;

    public FaviconView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
        ActualThemeChanged += (_, _) => RenderFallback();
        Unloaded += (_, _) => CancelLoad();
        RenderFallback();
    }

    public string? Domain
    {
        get => (string?)GetValue(DomainProperty);
        set => SetValue(DomainProperty, value);
    }
    public static readonly DependencyProperty DomainProperty = DependencyProperty.Register(
        nameof(Domain), typeof(string), typeof(FaviconView),
        new PropertyMetadata(null, (d, _) => ((FaviconView)d).Reload()));

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }
    public static readonly DependencyProperty DisplayNameProperty = DependencyProperty.Register(
        nameof(DisplayName), typeof(string), typeof(FaviconView),
        new PropertyMetadata(string.Empty, (d, _) => ((FaviconView)d).RenderFallback()));

    public VaultItemKind Kind
    {
        get => (VaultItemKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(VaultItemKind), typeof(FaviconView),
        new PropertyMetadata(VaultItemKind.Login, (d, _) => ((FaviconView)d).Reload()));

    public string FallbackGlyph
    {
        get => (string)GetValue(FallbackGlyphProperty);
        set => SetValue(FallbackGlyphProperty, value);
    }
    public static readonly DependencyProperty FallbackGlyphProperty = DependencyProperty.Register(
        nameof(FallbackGlyph), typeof(string), typeof(FaviconView),
        new PropertyMetadata(string.Empty, (d, _) => ((FaviconView)d).RenderFallback()));

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }
    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FaviconView),
        new PropertyMetadata(16d, (d, _) => ((FaviconView)d).RenderFallback()));

    public double TileSize
    {
        get => (double)GetValue(TileSizeProperty);
        set => SetValue(TileSizeProperty, value);
    }
    public static readonly DependencyProperty TileSizeProperty = DependencyProperty.Register(
        nameof(TileSize), typeof(double), typeof(FaviconView),
        new PropertyMetadata(32d, (d, _) => ((FaviconView)d).RenderFallback()));

    private void RenderFallback()
    {
        if (TileBorder is null)
            return;
        TileBorder.Width = TileBorder.Height = TileSize;
        TileBorder.CornerRadius = new CornerRadius(TileSize >= 56 ? 10 : TileSize >= 40 ? 8 : 6);
        GlyphIcon.Glyph = FallbackGlyph ?? string.Empty;
        GlyphIcon.FontSize = IconSize;
        IconImage.Width = IconImage.Height = Math.Max(IconSize, TileSize * .625);
        var name = string.IsNullOrWhiteSpace(DisplayName) ? Domain ?? string.Empty : DisplayName;
        var letter = Kind == VaultItemKind.Login && !string.IsNullOrWhiteSpace(name);
        LetterText.Text = letter ? StringInfo.GetNextTextElement(name.Trim()).ToUpperInvariant() : string.Empty;
        LetterText.FontSize = TileSize >= 56 ? 24 : TileSize >= 48 ? 20 : 14;
        LetterText.Visibility = letter ? Visibility.Visible : Visibility.Collapsed;
        GlyphIcon.Visibility = letter ? Visibility.Collapsed : Visibility.Visible;
        var dark = ActualTheme == ElementTheme.Dark;
        if (letter && AppPreferences.Current.ShowWebsiteIcons)
        {
            var (hue, chroma) = SiteColor(name, Domain);
            TileBorder.Background = new SolidColorBrush(chroma == 0
                ? (dark ? Microsoft.UI.ColorHelper.FromArgb(255, 58, 58, 58) : Microsoft.UI.ColorHelper.FromArgb(255, 230, 230, 230))
                : Oklch(dark ? .37 : .935, (dark ? .07 : .045) * chroma, hue));
            LetterText.Foreground = new SolidColorBrush(chroma == 0
                ? (dark ? Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243) : Microsoft.UI.ColorHelper.FromArgb(255, 38, 38, 38))
                : Oklch(dark ? .9 : .45, (dark ? .075 : .13) * chroma, hue));
        }
        else
        {
            TileBorder.Background = new SolidColorBrush(dark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 54, 54, 54)
                : Microsoft.UI.ColorHelper.FromArgb(255, 239, 239, 239));
            var foreground = ThemeBrushResolver.Resolve(this, TileSize >= 48
                ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            LetterText.Foreground = foreground;
            GlyphIcon.Foreground = foreground;
        }
        if (IconImage.Visibility == Visibility.Visible)
            LetterText.Visibility = GlyphIcon.Visibility = Visibility.Collapsed;
    }

    private static (double Hue, double Chroma) SiteColor(string name, string? domain)
    {
        var site = (domain ?? name).ToLowerInvariant();
        if (site.Contains("github", StringComparison.Ordinal)) return (0, 0);
        if (site.Contains("cloudflare", StringComparison.Ordinal)) return (55, 1);
        if (site.Contains("amazon", StringComparison.Ordinal) || site.Contains("aws", StringComparison.Ordinal)) return (75, 1);
        if (site.Contains("aliyun", StringComparison.Ordinal)) return (45, 1);
        if (site.Contains("proxmox", StringComparison.Ordinal)) return (32, 1);
        if (site.Contains("synology", StringComparison.Ordinal)) return (250, .45);
        if (site.Contains("docker", StringComparison.Ordinal)) return (232, 1);
        if (site.Contains("npm", StringComparison.Ordinal)) return (25, 1);
        if (site.Contains("netflix", StringComparison.Ordinal)) return (18, 1);
        if (site.Contains("steam", StringComparison.Ordinal)) return (262, 1);
        if (site.Contains("google", StringComparison.Ordinal)) return (255, 1);
        if (site.Contains("microsoft", StringComparison.Ordinal)) return (222, 1);
        uint hash = 2166136261;
        foreach (var character in name)
            hash = unchecked((hash ^ character) * 16777619);
        return (hash % 360, .8);
    }

    private static Windows.UI.Color Oklch(double lightness, double chroma, double hue)
    {
        var radians = hue * Math.PI / 180;
        var a = chroma * Math.Cos(radians);
        var b = chroma * Math.Sin(radians);
        var l = Math.Pow(lightness + .3963377774 * a + .2158037573 * b, 3);
        var m = Math.Pow(lightness - .1055613458 * a - .0638541728 * b, 3);
        var s = Math.Pow(lightness - .0894841775 * a - 1.291485548 * b, 3);
        static byte Channel(double value) => (byte)Math.Round(255 * Math.Clamp(
            value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055, 0, 1));
        return Microsoft.UI.ColorHelper.FromArgb(255,
            Channel(4.0767416621 * l - 3.3077115913 * m + .2309699292 * s),
            Channel(-1.2684380046 * l + 2.6097574011 * m - .3413193965 * s),
            Channel(-.0041960863 * l - .7034186147 * m + 1.707614701 * s));
    }

    private void CancelLoad()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async void Reload()
    {
        if (IconImage is null)
            return;
        CancelLoad();
        IconImage.Visibility = Visibility.Collapsed;
        IconImage.Source = null;
        RenderFallback();
        var domain = Domain;
        if (!IsLoaded || Kind != VaultItemKind.Login || !AppPreferences.Current.ShowWebsiteIcons || string.IsNullOrWhiteSpace(domain))
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            var cache = global::App.App.Services.GetRequiredService<IFaviconCache>();
            var bytes = await cache.GetAsync(domain, token);
            if (token.IsCancellationRequested || bytes is not { Length: > 0 })
                return;
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (token.IsCancellationRequested || !IsLoaded)
                return;
            IconImage.Source = bitmap;
            IconImage.Visibility = Visibility.Visible;
            LetterText.Visibility = GlyphIcon.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // Unavailable or invalid favicons keep the local, deterministic fallback.
        }
    }

    private void OnImageOpened(object sender, RoutedEventArgs e)
    {
        if (IconImage.Source is null || !IsLoaded)
            return;
        IconImage.Visibility = Visibility.Visible;
        LetterText.Visibility = GlyphIcon.Visibility = Visibility.Collapsed;
    }

    private void OnImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        IconImage.Visibility = Visibility.Collapsed;
        RenderFallback();
    }
}
