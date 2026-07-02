namespace App.Services;

/// <summary>
/// 托盘隐藏判定的纯逻辑：仅当托盘图标可见时，才允许「最小化/关闭到托盘」把窗口隐藏，
/// 否则窗口会被隐藏到不存在的托盘而无法找回。抽成纯函数以便单测（WinUI glue 不可测）。
/// </summary>
public static class TrayPolicy
{
    /// <summary>最小化时是否应隐藏窗口到托盘。</summary>
    public static bool ShouldHideOnMinimize(bool showIcon, bool minimizeToTray) => showIcon && minimizeToTray;

    /// <summary>关闭窗口时是否应隐藏到托盘（而非真正退出）。</summary>
    public static bool ShouldHideOnClose(bool showIcon, bool closeToTray) => showIcon && closeToTray;
}
