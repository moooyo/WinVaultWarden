using Microsoft.UI.Xaml;
using App.Services;

namespace App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        Services = ServiceConfiguration.Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
