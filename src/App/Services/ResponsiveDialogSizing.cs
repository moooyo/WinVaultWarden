namespace App.Services;

/// <summary>
/// 对话框响应式宽度：在首选宽度与 XamlRoot 可用宽度(减去水平边距)之间取较小值，
/// 保证高 DPI/窄窗口下对话框不发生水平裁切；下限钳到 0，非有限输入直接拒绝。
/// </summary>
public static class ResponsiveDialogSizing
{
    public static double Clamp(
        double rootWidth,
        double preferredWidth,
        double horizontalMargin = 48)
    {
        if (!double.IsFinite(rootWidth)
            || !double.IsFinite(preferredWidth)
            || !double.IsFinite(horizontalMargin))
            throw new ArgumentOutOfRangeException();

        return Math.Max(
            0,
            Math.Min(
                Math.Max(0, preferredWidth),
                rootWidth - Math.Max(0, horizontalMargin)));
    }
}
