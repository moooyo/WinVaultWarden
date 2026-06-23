using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class GeneratorDialog : ContentDialog
{
    public GeneratorViewModel ViewModel { get; }

    public GeneratorDialog()
    {
        ViewModel = global::App.App.Services.GetRequiredService<GeneratorViewModel>();
        InitializeComponent();
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        ViewModel.CopyCommand.Execute(null);
}
