using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace App.Converters;

// bool → Visibility。true=Visible,false=Collapsed。
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}
