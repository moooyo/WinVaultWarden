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
        GeneratorTabs.SelectedItem ??= PasswordTab;
        UpdatePrimaryButtonState();
    }

    private void OnGeneratorTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Mode = GeneratorTabs.SelectedIndex switch
        {
            1 => GeneratorMode.Passphrase,
            2 => GeneratorMode.Username,
            _ => GeneratorMode.Password,
        };
        UpdatePrimaryButtonState();
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ViewModel.CopyCommand.Execute(null);
    }

    private void UpdatePrimaryButtonState() => IsPrimaryButtonEnabled = true;

    private async void OnHistoryClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var dialog = new GeneratorHistoryDialog(ViewModel) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
