using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace App.Views;

public sealed partial class SendPage : Page
{
    public SendViewModel ViewModel { get; }
    private readonly AsyncOperationGate _receivedFileSaveGate = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _availabilityTimer;
    private CancellationTokenSource? _pageLifetimeCts;
    private int _receivedFileSaveInProgress;

    public SendPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SendViewModel>();
        InitializeComponent();
        _availabilityTimer = DispatcherQueue.CreateTimer();
        _availabilityTimer.Interval = TimeSpan.FromSeconds(30);
        _availabilityTimer.Tick += (_, _) => ViewModel.RefreshAvailability(DateTimeOffset.UtcNow);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ReplacePageLifetime();
        _availabilityTimer.Start();
        SetReceivedFileSaveBusy(Volatile.Read(ref _receivedFileSaveInProgress) != 0);
        if (e.Parameter is string tag)
            ViewModel.SelectFilterByTag(tag);
        _ = ViewModel.LoadAsync();
    }

    // 离开页面时取消进行中的加载,使迟到结果被丢弃、不再写回已被替换的页面 VM。
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        CancelPageLifetime();
        _availabilityTimer.Stop();
        ViewModel.CancelLoad();
        ViewModel.CancelReceive();
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
        if (item is null || !ViewModel.CanModifySend(item))
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
        var expectedSession = draft.OriginatingSession;
        if (expectedSession is null)
        {
            if (!TryCaptureExpectedLease(out expectedSession))
            {
                draft.ClearSensitive();
                return;
            }
            draft.OriginatingSession = expectedSession;
        }

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
            await DialogHelper.ShowTrackedAsync(
                dialog,
                expectedSession,
                draft.ClearSensitive);
        }
        finally
        {
            draft.ClearSensitive();
        }
    }

    private async void OnDeleteSendClick(object sender, RoutedEventArgs e)
    {
        var item = ItemFromSender(sender) ?? ViewModel.SelectedMenuItem;
        if (item is null || !ViewModel.CanModifySend(item))
            return;
        var expectedSession = ViewModel.LoadedSession;
        if (expectedSession is null)
            return;

        var root = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot;
        if (await DialogHelper.ConfirmAsync(
                root,
                expectedSession,
                "删除 Send",
                $"确定要删除“{item.Name}”吗？此操作无法撤销。",
                "删除"))
        {
            ViewModel.DeleteSendCommand.Execute(new SendDeleteRequest(item, expectedSession));
        }
    }

    private async void OnSaveReceivedFileClick(object sender, RoutedEventArgs e)
    {
        var received = ViewModel.LastReceived;
        if (received?.Accessed is not Core.Models.SendAccessResult accessed)
            return;
        if (!TryCaptureExpectedLease(out var expectedLease))
            return;
        if (!_receivedFileSaveGate.TryEnter(out var saveOperation))
            return;
        using (saveOperation)
        {
            var pageLifetime = _pageLifetimeCts;
            if (pageLifetime is null || pageLifetime.IsCancellationRequested)
                return;
            using var pageOperation = CancellationTokenSource.CreateLinkedTokenSource(
                pageLifetime.Token);
            Volatile.Write(ref _receivedFileSaveInProgress, 1);
            SetReceivedFileSaveBusy(true);
            try
            {
                using var download = accessed.AcquireDownloadLease();
                using var trackedDownload = global::App.App.Services
                    .GetRequiredService<global::App.Services.ITransientOperationRegistry>()
                    .Register(download.Dispose, () => { }, download.Dispose);
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                    SuggestedFileName = received.FileName ?? "send-file",
                };
                picker.FileTypeChoices.Add("文件", new System.Collections.Generic.List<string> { "." });
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await FilePickerHelper.PickSaveAsync(
                    picker,
                    pageOperation.Token);
                if (file is null)
                    return;

                var access = global::App.App.Services.GetRequiredService<Core.Services.ISendAccessService>();
                await FilePickerHelper.RunTrackedAsync<object?>(
                    async ct =>
                    {
                        using var transaction = await file.OpenTransactedWriteAsync().AsTask(ct);
                        await using var destination = transaction.Stream.AsStreamForWrite();
                        destination.SetLength(0);
                        destination.Position = 0;

                        await access.DownloadFileAsync(download, destination, ct);
                        await destination.FlushAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        await FilePickerHelper.CommitSensitiveAsync(
                            transaction,
                            expectedLease,
                            ct);
                        return null;
                    },
                    ct: pageOperation.Token);
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, pageOperation.Token))
            {
            }
            catch (Exception ex) when (IsRecoverableFileTransferException(ex))
            {
                if (!pageOperation.IsCancellationRequested)
                    ViewModel.Error = FileTransferError(ex);
            }
            finally
            {
                Volatile.Write(ref _receivedFileSaveInProgress, 0);
                SetReceivedFileSaveBusy(false);
            }
        }
    }

    private void SetReceivedFileSaveBusy(bool busy)
    {
        SaveReceivedFileButton.IsEnabled = !busy;
        ReceivedFileSaveProgressRing.IsActive = busy;
        ReceivedFileSaveProgressRing.Visibility = busy
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ReplacePageLifetime()
    {
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pageLifetimeCts, replacement);
        CancelAndDispose(previous);
    }

    private void CancelPageLifetime()
    {
        var previous = Interlocked.Exchange(ref _pageLifetimeCts, null);
        CancelAndDispose(previous);
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
            return;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
        }
        finally
        {
            source.Dispose();
        }
    }

    private static bool IsRecoverableFileTransferException(Exception exception) =>
        Api.RequestTimeoutClassifier.IsTimeout(exception)
        || exception is IOException
            or UnauthorizedAccessException
            or HttpRequestException
            or TimeoutException
            or InvalidDataException
            or System.Security.Cryptography.CryptographicException
            or ObjectDisposedException;

    private static string FileTransferError(Exception exception)
    {
        if (Api.RequestTimeoutClassifier.IsTimeout(exception))
            return "文件传输长时间没有进展，请检查连接后重试。";

        return exception switch
        {
            UnauthorizedAccessException => "没有访问所选文件的权限。",
            InvalidDataException or System.Security.Cryptography.CryptographicException =>
                "Send 文件完整性校验失败，文件未保存。",
            HttpRequestException => "网络请求失败，请检查连接后重试。",
            IOException => "文件写入失败，请检查磁盘状态后重试。",
            ObjectDisposedException => "操作已取消，文件未保存。",
            _ => "保存 Send 文件失败。",
        };
    }

    private static bool TryCaptureExpectedLease(out Core.Session.VaultSessionLease lease)
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        try
        {
            lease = session.CaptureLease();
            return session.IsUnlocked(lease);
        }
        catch (InvalidOperationException)
        {
            lease = null!;
            return false;
        }
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            ViewModel.SelectFilterByTag(tag);
    }

    private void OnReceiveClick(object sender, RoutedEventArgs e)
    {
        ReceivedSendExpander.Visibility = ReceivedSendExpander.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        ReceivedSendExpander.IsExpanded = true;
        if (ReceivedSendExpander.Visibility == Visibility.Visible)
            ReceivedLinkTextBox.Focus(FocusState.Programmatic);
    }
}