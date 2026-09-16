using System.ComponentModel;
using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class GeneratorWorkspace : UserControl
{
    public GeneratorViewModel ViewModel { get; }
    public event EventHandler? HistoryRequested;

    public GeneratorWorkspace()
    {
        ViewModel = global::App.App.Services.GetRequiredService<GeneratorViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PrepareForDisplay();
        Bindings.Update();
        UpdateFeedbackSeverity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GeneratorViewModel.CopyFailed))
            UpdateFeedbackSeverity();
    }

    private void UpdateFeedbackSeverity() => CopyFeedbackBar.Severity = ViewModel.CopyFailed
        ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    private void OnHistoryClick(object sender, RoutedEventArgs e) =>
        HistoryRequested?.Invoke(this, EventArgs.Empty);
}
