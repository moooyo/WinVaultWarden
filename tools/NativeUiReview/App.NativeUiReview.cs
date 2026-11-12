namespace App;

public partial class App
{
    internal App(IServiceProvider fixtureServices)
    {
        global::App.NativeUiReview.NativeReviewStartupTrace.Record("app-before-resource-runtime");
        // ReviewFixture has already installed synthetic, memory-only preferences. MRT Core's
        // Microsoft.Windows override is process-local; this does not use Windows.Globalization.
        global::App.Localization.AppLocalization.Initialize();
        global::App.NativeUiReview.NativeReviewStartupTrace.Record("app-resource-runtime-ready");
        InitializeComponent();
        global::App.NativeUiReview.NativeReviewStartupTrace.Record("app-xaml-initialized");
        Services = fixtureServices;
    }

    internal static void SetNativeReviewWindow(MainWindow window) => MainWindow = window;
}
