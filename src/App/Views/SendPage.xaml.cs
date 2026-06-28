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
        _ = ViewModel.LoadAsync();
    }

    private static SendListItem? ItemFromSender(object sender) =>
        sender is FrameworkElement { DataContext: SendListItem item } ? item : null;

    private async void OnAddSendClick(object sender, RoutedEventArgs e)
    {
        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;
        var dialog = new SendEditorDialog { XamlRoot = root };
        await dialog.ShowAsync();
        if (dialog.Saved)
            await ViewModel.CreateSendAsync(dialog.Draft, dialog.Draft.FileBytes);
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

    private async void OnEditSendClick(object sender, RoutedEventArgs e)
    {
        var item = ItemFromSender(sender) ?? ViewModel.SelectedMenuItem;
        if (item is null)
            return;

        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;
        var dialog = new SendEditorDialog(item) { XamlRoot = root };
        await dialog.ShowAsync();
        if (dialog.Saved)
            await ViewModel.UpdateSendFromDraftAsync(item, dialog.Draft, dialog.Draft.FileBytes);
    }

    private async void OnDeleteSendClick(object sender, RoutedEventArgs e)
    {
        var item = ItemFromSender(sender) ?? ViewModel.SelectedMenuItem;
        if (item is null)
            return;

        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;
        if (await DialogHelper.ConfirmAsync(root, "删除 Send", $"确定要删除"{item.Name}"吗？此操作无法撤销。", "删除"))
            ViewModel.DeleteSendCommand.Execute(item);
    }

    private async void OnSaveReceivedFileClick(object sender, RoutedEventArgs e)
    {
        var received = ViewModel.LastReceived;
        if (received?.Accessed is not Core.Models.SendAccessResult accessed)
            return;

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DownloadsFolder,
            SuggestedFileName = received.FileName ?? "send-file",
        };
        picker.FileTypeChoices.Add("文件", new System.Collections.Generic.List<string> { "." });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var access = global::App.App.Services.GetRequiredService<Core.Services.ISendAccessService>();
        var bytes = await access.DownloadFileAsync(accessed);
        await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
    }

    private static void SetRowActionsOpacity(object sender, double opacity)
    {
        if (sender is Grid root && root.FindName("RowActions") is FrameworkElement panel)
        {
            panel.Opacity = opacity;
            panel.IsHitTestVisible = opacity > 0;
        }
    }

    private void OnRowPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetRowActionsOpacity(sender, 1);
    private void OnRowPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetRowActionsOpacity(sender, 0);
    private void OnRowGotFocus(object sender, RoutedEventArgs e) => SetRowActionsOpacity(sender, 1);
    private void OnRowLostFocus(object sender, RoutedEventArgs e) => SetRowActionsOpacity(sender, 0);
}
