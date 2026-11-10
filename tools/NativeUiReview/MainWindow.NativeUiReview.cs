using App.NativeUiReview;
using App.Controls;
using App.Services;
using App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace App.NativeUiReview
{
    internal sealed class NativeReviewWindowMarker
    {
        internal static NativeReviewWindowMarker Instance { get; } = new();
        private NativeReviewWindowMarker() { }
    }
}

namespace App
{
    public sealed partial class MainWindow
    {
        internal MainWindow(NativeReviewWindowMarker marker)
        {
            ArgumentNullException.ThrowIfNull(marker);
            InitializeComponent();
            NativeReviewDisableNavigationTransitions(ContentFrame);
            NativeReviewDisableNavigationTransitions(LoginFrame);
            Title = "WinVaultWarden Native UI Review (synthetic)";
            _nav.Initialize(ContentFrame, LoginFrame);
            ContentFrame.Navigated += (_, _) => UpdateNavigationContext();
            global::App.App.Services.GetRequiredService<UiDispatcher>().Initialize(DispatcherQueue);
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            InitializeShell();
            ShellRoot.ActualThemeChanged += (_, _) => ShellRoot.Background =
                ThemeBrushResolver.Resolve(ShellRoot, "WardenMicaBrush");
            SizeChanged += (_, _) => UpdateCaptionPadding();
            UpdateCaptionPadding();
            // Do not register production startup, tray, system-session hooks or background hosts.
        }

        internal FrameworkElement NativeReviewRoot => ShellRoot;
        internal Frame NativeReviewFrame => ContentFrame;
        internal Frame NativeReviewLoginFrame => LoginFrame;
        internal bool NativeReviewIsAuth => LoginHost.Visibility == Visibility.Visible;
        internal bool NativeReviewNavigationCollapsed => Nav.Visibility == Visibility.Collapsed;
        internal string NativeReviewSearchQuery => TitleSearch.Text;
        internal void NativeReviewFocusChrome() => ThemeButton.Focus(FocusState.Programmatic);

        internal static void NativeReviewDisableNavigationTransitions(Frame frame) =>
            frame.ContentTransitions = new TransitionCollection
            {
                new NavigationThemeTransition { DefaultNavigationTransitionInfo = new SuppressNavigationTransitionInfo() },
            };

        internal void NativeReviewSetViewport(int width, int height, ElementTheme theme)
        {
            AppPreferences.Current.ThemeIndex = theme == ElementTheme.Dark ? 2 : 1;
            ShellRoot.RequestedTheme = theme;
            // RenderTargetBitmap cannot rasterize a system Mica backdrop. Use its production fallback token.
            ShellRoot.Background = ThemeBrushResolver.Resolve(ShellRoot, "WardenMicaBrush");
            var scale = ShellRoot.XamlRoot?.RasterizationScale ?? 1;
            AppWindow.ResizeClient(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
            UpdateCaptionPadding();
        }

        internal void NativeReviewCorrectViewport(int width, int height)
        {
            var scale = ShellRoot.XamlRoot.RasterizationScale;
            var current = AppWindow.Size;
            AppWindow.Resize(new SizeInt32(
                current.Width + (int)Math.Round((width - ShellRoot.ActualWidth) * scale),
                current.Height + (int)Math.Round((height - ShellRoot.ActualHeight) * scale)));
        }

        internal void NativeReviewShowVault()
        {
            _isLoginWindow = false;
            LoginHost.Visibility = Visibility.Collapsed;
            LoginFrame.Content = null;
            Nav.Visibility = Visibility.Visible;
            SetShellSessionVisibility(true);
            SetSensitiveUiEnabled(true);
            UpdateAccountContext();
            PopulateFolderNavigation();
            _nav.GoToVault("vault:allitems");
            RefreshShellNavigation(force: true);
            UpdateResponsiveShell();
        }

        internal void NativeReviewNavigate(string route)
        {
            if (NativeReviewIsAuth)
                NativeReviewShowVault();
            switch (route)
            {
                case "vault": _nav.GoToVault("vault:allitems"); break;
                case "generator": _nav.GoToGenerator(); break;
                case "send": _nav.GoToSend("send:all"); break;
                case "reports": _nav.GoToSecurityReport(); break;
                case "settings": _nav.GoToSettings(); break;
                default: throw new ArgumentOutOfRangeException(nameof(route));
            }
            RefreshShellNavigation(force: true);
            UpdateResponsiveShell();
        }

        internal void NativeReviewShowAuth(bool registration)
        {
            _isLoginWindow = true;
            ContentFrame.Content = null;
            ContentFrame.CacheSize = 0;
            Nav.Visibility = Visibility.Collapsed;
            LoginHost.Visibility = Visibility.Visible;
            SetShellSessionVisibility(false);
            if (registration) _nav.GoToRegister();
            else _nav.GoToLoginPage();
        }

        internal void NativeReviewRefreshShell()
        {
            RefreshShellNavigation(force: true);
            UpdateResponsiveShell();
        }

        internal string? NativeReviewNavigationCount(string tag) => FindNavigationItem(tag)?.Content is Grid grid
            ? grid.Children.OfType<TextBlock>().LastOrDefault()?.Text : null;

        internal bool NativeReviewNavigationCountVisible(string tag)
        {
            if (FindNavigationItem(tag) is not { Content: Grid grid } item
                || grid.Children.OfType<TextBlock>().LastOrDefault() is not { ActualWidth: > 0 } count)
                return false;
            var bounds = count.TransformToVisual(item).TransformBounds(
                new Windows.Foundation.Rect(0, 0, count.ActualWidth, count.ActualHeight));
            return bounds.X >= 0 && bounds.Right <= item.ActualWidth + 1;
        }

        internal void NativeReviewQuiesce()
        {
            _statusTimer?.Stop();
            _idleTimer?.Stop();
            _timeout.Stop();
            global::App.App.Services.GetRequiredService<ITransientOperationRegistry>().CancelAndCloseAllSynchronously();
            if (ContentFrame.Content is VaultPage vault) vault.NativeReviewCloseFolderFlyout();
            ContentFrame.CacheSize = 0;
            ContentFrame.Content = null;
            LoginFrame.Content = null;
        }
    }
}
