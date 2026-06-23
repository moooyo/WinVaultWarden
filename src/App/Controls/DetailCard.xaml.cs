using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Controls;

public sealed partial class DetailCard : UserControl
{
    public DetailCard() => InitializeComponent();

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(DetailCard),
            new PropertyMetadata("", (d, e) => ((DetailCard)d).TitleText.Text = (string)e.NewValue));

    public object? CardContent
    {
        get => GetValue(CardContentProperty);
        set => SetValue(CardContentProperty, value);
    }
    public static readonly DependencyProperty CardContentProperty =
        DependencyProperty.Register(nameof(CardContent), typeof(object), typeof(DetailCard),
            new PropertyMetadata(null));
}
