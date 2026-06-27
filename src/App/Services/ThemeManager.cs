using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace App.Services;

/// <summary>
/// 集中处理主题切换:把主题索引(0=跟随系统/1=浅色/2=深色)映射为 <see cref="ElementTheme"/>,
/// 应用到主窗口内容根,并让自定义标题栏的 caption 按钮(最小化/最大化/关闭)配色跟随。
/// 由设置页(用户切换)与 App.OnLaunched(启动回放持久化值)共同调用。
/// </summary>
public static class ThemeManager
{
    public static void Apply(int index)
    {
        var window = global::App.App.MainWindow;
        if (window?.Content is not FrameworkElement root)
            return;

        var theme = index switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        root.RequestedTheme = theme;
        ApplyCaptionButtonColors(window, theme);
    }

    private static void ApplyCaptionButtonColors(Window window, ElementTheme theme)
    {
        var titleBar = window.AppWindow.TitleBar;

        // 跟随系统:清空覆盖,交还系统默认配色。
        if (theme == ElementTheme.Default)
        {
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            return;
        }

        var dark = theme == ElementTheme.Dark;
        var foreground = dark ? Colors.White : Colors.Black;
        var hoverOverlay = dark ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                                : Color.FromArgb(0x14, 0x00, 0x00, 0x00);

        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
                                                       : Color.FromArgb(0xFF, 0x60, 0x60, 0x60);
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonHoverBackgroundColor = hoverOverlay;
    }
}
