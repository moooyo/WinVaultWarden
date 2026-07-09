using App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace App.Converters;

/// <summary>
/// SecuritySeverity → 语义色刷子(Good=Success/Caution=Caution/Critical=Critical),
/// 用于安全报告页 Hero 卡片的盾牌图标着色。
/// </summary>
public sealed partial class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is SecuritySeverity severity
            ? severity switch
            {
                SecuritySeverity.Critical => "SystemFillColorCriticalBrush",
                SecuritySeverity.Caution => "SystemFillColorCautionBrush",
                _ => "SystemFillColorSuccessBrush",
            }
            : "SystemFillColorSuccessBrush";

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
