using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class VaultPage : Page
{
    public VaultViewModel ViewModel { get; }

    public VaultPage()
    {
        ViewModel = App.Services.GetRequiredService<VaultViewModel>();
        InitializeComponent();
    }
}
