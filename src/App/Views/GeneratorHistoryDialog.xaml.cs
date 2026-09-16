using System.ComponentModel;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class GeneratorHistoryDialog : ContentDialog
{
    public GeneratorViewModel ViewModel { get; }
    public bool ClearRequested { get; private set; }

    public GeneratorHistoryDialog(GeneratorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            Bindings.StopTracking();
            HistoryList.ItemsSource = null;
        };
        SizeChanged += (_, _) => ApplyResponsiveWidth();
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Bindings.Update();
        ApplyResponsiveWidth();
        UpdateState();
    }

    private void ApplyResponsiveWidth()
    {
        if (XamlRoot is null)
            return;
        GeneratorHistoryContent.Width = ResponsiveDialogSizing.Clamp(
            XamlRoot.Size.Width, preferredWidth: 512, horizontalMargin: 96);
        GeneratorHistoryContent.MaxHeight = Math.Max(0, XamlRoot.Size.Height - 220);
        HistoryList.MaxHeight = Math.Max(0, XamlRoot.Size.Height - 340);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        IsPrimaryButtonEnabled = ViewModel.HasHistory;
        CopyFeedbackBar.Severity = ViewModel.CopyFailed ? InfoBarSeverity.Error : InfoBarSeverity.Success;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GeneratorHistoryItem item })
            ViewModel.CopyHistoryItemCommand.Execute(item);
    }

    // Confirmation is displayed by the host after this dialog has closed.
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ViewModel.HasHistory)
        {
            args.Cancel = true;
            return;
        }
        ClearRequested = true;
        Hide();
    }
}
