using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace App.Views;

public sealed partial class SendPage : Page
{
    public SendViewModel ViewModel { get; }

    public SendPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SendViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string tag)
            ViewModel.SelectFilterByTag(tag);
    }

    private static SendListItem? ItemFromSender(object sender) =>
        sender is FrameworkElement { DataContext: SendListItem item } ? item : null;

    private async void OnAddSendClick(object sender, RoutedEventArgs e)
    {
        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;
        var dialog = new SendEditorDialog { XamlRoot = root };
        await dialog.ShowAsync();
        if (dialog.Saved)
            ViewModel.CreateSend(dialog.Draft);
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CopyLinkCommand.Execute(ItemFromSender(sender));
    }

    private void OnCopyLinkMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SendListItem item })
            ViewModel.CopyLinkCommand.Execute(item);
        else
            ViewModel.CopyLinkCommand.Execute(ViewModel.SelectedMenuItem);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        ViewModel.MarkMoreMenuOpened(ItemFromSender(sender));
    }
}
