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

    private void OnGeneratorTabSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdatePrimaryButtonState();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (IsPasswordTabSelected())
            ViewModel.CopyCommand.Execute(null);
    }

    private void UpdatePrimaryButtonState() =>
        IsPrimaryButtonEnabled = IsPasswordTabSelected();

    private bool IsPasswordTabSelected() =>
        GeneratorTabs is not null
        && PasswordTab is not null
        && GeneratorTabs.SelectedItem is TabViewItem selectedTab
        && ReferenceEquals(selectedTab, PasswordTab);
}
