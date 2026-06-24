using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class GeneratorHistoryDialog : ContentDialog
{
    public GeneratorViewModel ViewModel { get; }

    public GeneratorHistoryDialog(GeneratorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GeneratorHistoryItem item })
            ViewModel.CopyHistoryItemCommand.Execute(item);
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ViewModel.ClearHistoryCommand.Execute(null);
    }
}
