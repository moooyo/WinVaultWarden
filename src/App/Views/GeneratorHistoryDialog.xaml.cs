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

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var confirmed = await DialogHelper.ConfirmAsync(
                XamlRoot,
                "清除历史记录",
                "确定要清除全部生成历史吗?此操作无法撤销。",
                "清除");
            if (!confirmed)
            {
                args.Cancel = true;
                return;
            }

            ViewModel.ClearHistoryCommand.Execute(null);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
