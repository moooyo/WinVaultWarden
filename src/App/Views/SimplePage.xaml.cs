using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace App.Views;

public sealed partial class SimplePage : Page
{
    public SimplePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ValueTuple<string, string> p)
        {
            Title.Text = p.Item1;
            Icon.Glyph = p.Item2;
            Subtitle.Text = $"{p.Item1}正在开发中";
        }
    }
}
