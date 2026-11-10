namespace App;

public partial class App
{
    internal App(IServiceProvider fixtureServices)
    {
        InitializeComponent();
        Services = fixtureServices;
    }

    internal static void SetNativeReviewWindow(MainWindow window) => MainWindow = window;
}
