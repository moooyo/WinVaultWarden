using App.ViewModels;
using App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace App.Views;

public sealed partial class SecurityReportPage : Page
{
    public SecurityReportViewModel ViewModel { get; }

    public SecurityReportPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SecurityReportViewModel>();
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadOfflineAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelOfflineAnalysis();
        ViewModel.CancelExposedCheck();
        base.OnNavigatedFrom(e);
    }

    private void OnOpenItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cipherId } && !string.IsNullOrWhiteSpace(cipherId))
            global::App.App.Services.GetRequiredService<INavigationService>().GoToVault("vault:item:" + cipherId);
    }

    public static string FormatOpenItem(string name) => $"查看条目：{name}";

    /// <summary>Formats a group without exposing the reused password.</summary>
    public static string FormatReusedHeader(int count) => $"{count} 个条目共用同一密码";

    /// <summary>Shows a clean result only after a successful check.</summary>
    public static Visibility ShowNoExposedFound(bool exposedChecked, bool hasExposed) =>
        exposedChecked && !hasExposed ? Visibility.Visible : Visibility.Collapsed;
}
