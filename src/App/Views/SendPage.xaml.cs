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

    // 离开页面时取消进行中的加载,使迟到结果被丢弃、不再写回已被替换的页面 VM。
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.CancelLoad();
    }

    /// <summary>供 NotificationsHost 在收到 SendsChanged 推送后从 UI 线程调用，重新加载 Send 列表。</summary>
    public void RefreshSendList() => _ = ViewModel.LoadAsync();

    private static SendListItem? ItemFromSender(object sender) =>
        sender is FrameworkElement { DataContext: SendListItem item } ? item : null;

    private async void OnAddSendClick(object sender, RoutedEventArgs e)
    {
        await ShowEditorAsync(null, SendEditorDraft.CreateDefault(SendType.Text));
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

        // F1:打开编辑器前拉取完整解密 Send 并水合 draft(而非仅用列表项的 thin 字段),
        // 避免保存时把备注/密码/次数/过期/隐藏邮箱/文本等未加载字段覆盖为默认值。
        var draft = await ViewModel.GetEditDraftAsync(item.Id);
        if (draft is null)
            return;

        await ShowEditorAsync(item, draft);
    }

    // 打开 Send 编辑器:保存在对话框 Primary deferral 内完成(创建/更新委托),对话框关闭后不再另行上传。
    // item 为 null → 新增;否则 → 编辑。无论成败,关闭后清空草稿敏感值(文本/密码/备注/文件源/wrapped key)。
    private async Task ShowEditorAsync(SendListItem? item, SendEditorDraft draft)
    {
        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;
        var dialog = new SendEditorDialog(
            draft,
            isEditMode: item is not null,
            async (value, ct) => item is null
                ? await ViewModel.CreateSendAsync(value, value.ReplacementFile, ct)
                : await ViewModel.UpdateSendFromDraftAsync(item, value, value.ReplacementFile, ct))
        {
            XamlRoot = root,
        };
        try
        {
            await DialogHelper.ShowTrackedAsync(dialog, draft.ClearSensitive);
        }
        finally
        {
            draft.ClearSensitive();
        }
    }

    private async void OnDeleteSendClick(object sender, RoutedEventArgs e)
    {
        var item = ItemFromSender(sender) ?? ViewModel.SelectedMenuItem;
        if (item is null)
            return;

        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;
        if (await DialogHelper.ConfirmAsync(root, "删除 Send", $"确定要删除“{item.Name}”吗？此操作无法撤销。", "删除"))
            ViewModel.DeleteSendCommand.Execute(item);
    }

    private async void OnSaveReceivedFileClick(object sender, RoutedEventArgs e)
    {
        var received = ViewModel.LastReceived;
        if (received?.Accessed is not Core.Models.SendAccessResult accessed)
            return;

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            SuggestedFileName = received.FileName ?? "send-file",
        };
        picker.FileTypeChoices.Add("文件", new System.Collections.Generic.List<string> { "." });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await FilePickerHelper.PickSaveAsync(picker);
        if (file is null)
            return;

        var access = global::App.App.Services.GetRequiredService<Core.Services.ISendAccessService>();
        var bytes = await access.DownloadFileAsync(accessed);
        await FilePickerHelper.RunTrackedAsync<object?>(
            async ct =>
            {
                await Windows.Storage.FileIO.WriteBytesAsync(file, bytes).AsTask(ct);
                return null;
            });
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
