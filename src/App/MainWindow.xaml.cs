using App.Views;
using Microsoft.UI.Xaml;

namespace App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RootFrame.Navigate(typeof(LoginPage));
    }
}
