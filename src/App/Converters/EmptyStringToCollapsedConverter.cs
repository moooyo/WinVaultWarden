using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace App.Converters;

// 字符串 null/空 → Collapsed,否则 Visible。用于备注等可选字段。
public sealed class EmptyStringToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
