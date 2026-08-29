using App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace App.Converters;

/// <summary>
/// Maps security-report severity to the matching semantic theme brush.
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
                SecuritySeverity.Good => "SystemFillColorSuccessBrush",
                _ => "TextFillColorSecondaryBrush",
            }
            : "TextFillColorSecondaryBrush";

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
