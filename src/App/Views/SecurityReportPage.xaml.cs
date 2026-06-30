using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadOffline();
    }
}
