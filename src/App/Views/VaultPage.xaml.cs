using System;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using App.Controls;
using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Runtime.InteropServices.WindowsRuntime;

namespace App.Views;

public sealed partial class VaultPage : Page
{
    public VaultViewModel ViewModel { get; }

    public VaultPage()
    {
        // 用完全限定名避免与命名空间 App.Services 冲突(App 类的静态属性 Services)。
        ViewModel = global::App.App.Services.GetRequiredService<VaultViewModel>();
        InitializeComponent();
        GroupedItemsSource.Source = ViewModel.GroupedItems;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FoldersChanged += OnVaultFoldersChanged;
        UpdateDetailTemplate();
    }

    /// <summary>供 NotificationsHost 在收到 VaultChanged 推送后从 UI 线程调用，刷新列表快照。</summary>
    public void RefreshVaultList() => ViewModel.RefreshFromSnapshot();

    public static Visibility VisibleIfTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static ListViewSelectionMode SelectionModeFromBool(bool isSelectionMode) =>
        isSelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        // 与 SelectAllCommand(填 _selectedIds)并行:让 ListView 也视觉全选,保持勾选与计数一致。
        if (ViewModel.IsSelectionMode)
            CipherList.SelectAll();
    }

    // 多选模式下把 ListView 的勾选/取消精确同步进 VM 的选中集。
    // 此前 CipherList 只单向绑定 SelectedItem、无此接线,逐项勾选完全不被记录,批量操作全程禁用(R4)。
    private void OnCipherSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.IsSelectionMode)
            return;
        foreach (var removed in e.RemovedItems)
            if (removed is CipherListItem item)
                ViewModel.SetItemSelected(item.Id, selected: false);
        foreach (var added in e.AddedItems)
            if (added is CipherListItem item)
                ViewModel.SetItemSelected(item.Id, selected: true);
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

    private static CipherListItem? RowItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as CipherListItem;

    private async void OnRowCopyPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is not { } item)
            return;
        if (ViewModel.RequiresRepromptFor(item.Id) && !await PromptAndVerifyRepromptAsync())
            return;
        ViewModel.CopyPrimaryCommand.Execute(item.Id);
    }

    // 弹主密码对话框并回填 ViewModel.VerifyRepromptAsync;通过返回 true。与 ImportExportPage 的
    // 主密码确认对话框同一模式(循环重试、错误提示、SystemFillColorCriticalBrush)。
    private async Task<bool> PromptAndVerifyRepromptAsync()
    {
        var passwordBox = new PasswordBox { PlaceholderText = "主密码", MinWidth = 320 };
        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "该条目要求主密码重提示", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(passwordBox);
        panel.Children.Add(errorText);
        var dialog = new ContentDialog
        {
            Title = "验证主密码",
            Content = panel,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        while (true)
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;
            if (await ViewModel.VerifyRepromptAsync(passwordBox.Password))
                return true;
            errorText.Text = "主密码不正确";
            errorText.Visibility = Visibility.Visible;
        }
    }

    // PasswordHistoryItem/CustomField 行嵌套在子级 DataTemplate 内,其 DataContext 类型不携带
    // Reprompt——WinUI 的 x:Bind 与经典 ElementName Binding 均无法跨 DataTemplate 边界读取外层
    // Page 的属性(命名空间作用域限制),因此改为在 DataContext 就位/复用(含虚拟化容器回收)时
    // 由代码隐藏直接从 ViewModel.Detail 回填,而非尝试跨模板绑定。
    private void OnSecretFieldRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is FieldRow row)
            row.RepromptRequired = ViewModel.Detail?.Reprompt ?? false;
    }

    private void OnRemoveCustomFieldClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CustomFieldEditorDraft field)
            ViewModel.RemoveCustomFieldCommand.Execute(field);
    }

    private void OnRowEditClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
        {
            ViewModel.BeginEdit(item.Id);
            SyncEditorTypeSelection();
        }
    }

    private async void OnRowFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await ViewModel.ToggleFavoriteAsync(item.Id);
    }

    private async void OnRowSoftDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item
            && await ConfirmAsync("移到回收站", $"确定要将“{item.Name}”移到回收站吗?", "删除"))
            await ViewModel.SoftDeleteAsync(item.Id);
    }

    private async void OnRowRestoreClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await ViewModel.RestoreAsync(item.Id);
    }

    private async void OnRowPermanentDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item
            && await ConfirmAsync("永久删除", $"确定要永久删除“{item.Name}”吗?此操作无法撤销。", "永久删除"))
            await ViewModel.PermanentDeleteAsync(item.Id);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string tag)
            ViewModel.SelectFilterByTag(tag);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultViewModel.Detail))
            UpdateDetailTemplate();
    }

    private void OnAddLoginClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Login);

    private void OnAddCardClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Card);

    private void OnAddIdentityClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Identity);

    private void OnAddNoteClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Note);

    private void OnAddSshClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Ssh);

    private void BeginAdd(VaultItemKind kind)
    {
        ViewModel.BeginAdd(kind);
        SyncEditorTypeSelection();
    }

    private void OnCancelCipherEditorClick(object sender, RoutedEventArgs e) => ViewModel.CancelEdit();

    private async void OnSaveCipherEditorClick(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.SaveDraftAsync())
            UpdateDetailTemplate();
    }

    private void OnCipherEditorTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.IsEditing || CipherEditorTypeBox.SelectedItem is not ComboBoxItem item)
            return;

        if (item.Tag is string tag && Enum.TryParse(tag, out VaultItemKind kind))
        {
            ViewModel.ChangeEditorType(kind);
        }
    }

    private void SyncEditorTypeSelection()
    {
        if (ViewModel.EditorDraft is null)
            return;

        var tag = ViewModel.EditorDraft.Type.ToString();
        foreach (var item in CipherEditorTypeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == tag)
            {
                CipherEditorTypeBox.SelectedItem = item;
                return;
            }
        }
    }

    // 按 Detail 运行时类型从 Page.Resources 取对应 DataTemplate。
    private void UpdateDetailTemplate()
    {
        var detail = ViewModel.Detail;
        if (detail is null)
        {
            DetailHost.Content = null;
            DetailHost.ContentTemplate = null;
            return;
        }

        var key = detail switch
        {
            LoginDetail => "LoginTemplate",
            CardDetail => "CardTemplate",
            IdentityDetail => "IdentityTemplate",
            NoteDetail => "NoteTemplate",
            SshDetail => "SshTemplate",
            _ => null,
        };

        DetailHost.ContentTemplate = key is not null ? Resources[key] as DataTemplate : null;
        DetailHost.Content = detail;
    }

    private void OnMoveSelectedFlyoutOpening(object sender, object e)
    {
        MoveFolderFlyout.Items.Clear();

        var noFolder = new MenuFlyoutItem { Text = "无文件夹" };
        noFolder.Click += (_, _) => ViewModel.MoveSelectedToFolderCommand.Execute(null);
        MoveFolderFlyout.Items.Add(noFolder);

        foreach (var folder in ViewModel.FolderFilters)
        {
            var item = new MenuFlyoutItem { Text = folder.Label };
            var folderId = folder.FolderId;
            item.Click += (_, _) => ViewModel.MoveSelectedToFolderCommand.Execute(folderId);
            MoveFolderFlyout.Items.Add(item);
        }
    }

    private async void OnPermanentDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.SelectedCount;
        if (count == 0)
            return;

        var dialog = new ContentDialog
        {
            Title = "永久删除所选项目？",
            Content = $"此操作不可撤销，将从回收站彻底移除 {count} 项。",
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.PermanentDeleteSelectedCommand.ExecuteAsync(null);
    }

    private void OnVaultFoldersChanged(object? sender, EventArgs e) =>
        global::App.App.MainWindow?.RefreshFolderNavigation();

    private void OnEditCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
        {
            ViewModel.BeginEdit(item.Id);
            SyncEditorTypeSelection();
        }
    }

    private async void OnDeleteCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;
        if (await ConfirmAsync("移到回收站", $"确定要将“{item.Name}”移到回收站吗?", "删除"))
            await ViewModel.SoftDeleteAsync(item.Id);
    }

    private async void OnRestoreCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ViewModel.RestoreAsync(item.Id);
    }

    private async void OnPermanentDeleteCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;
        if (await ConfirmAsync("永久删除", $"确定要永久删除“{item.Name}”吗?此操作无法撤销。", "永久删除"))
            await ViewModel.PermanentDeleteAsync(item.Id);
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync("新建文件夹", "文件夹名称", string.Empty);
        if (!string.IsNullOrWhiteSpace(name))
            await ViewModel.SaveFolderAsync(null, name!);
    }

    private async void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFilter is not { Kind: FilterKind.Folder } folder || folder.FolderId is null)
            return;
        var name = await PromptTextAsync("重命名文件夹", "文件夹名称", folder.Label);
        if (!string.IsNullOrWhiteSpace(name))
            await ViewModel.SaveFolderAsync(folder.FolderId, name!);
    }

    private async void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFilter is not { Kind: FilterKind.Folder } folder || folder.FolderId is null)
            return;
        if (await ConfirmAsync("删除文件夹", $"确定要删除文件夹“{folder.Label}”吗?其中的项目会移出该文件夹。", "删除"))
            await ViewModel.DeleteFolderAsync(folder.FolderId);
    }

    private Task<bool> ConfirmAsync(string title, string message, string primaryText) =>
        DialogHelper.ConfirmAsync(XamlRoot, title, message, primaryText);

    private async void OnEmptyRecycleBinClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasTrashItems)
            return;
        if (await ConfirmAsync("清空回收站",
                $"确定永久删除回收站中的全部 {ViewModel.TrashCount} 项？此操作无法撤销。", "永久删除"))
            await ViewModel.EmptyRecycleBinCommand.ExecuteAsync(null);
    }

    private async Task<string?> PromptTextAsync(string title, string placeholder, string initial)
    {
        var input = new TextBox { PlaceholderText = placeholder, Text = initial };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }

    private async void OnAddAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;

        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");

        // WinUI 3 桌面应用:必须把 picker 关联到本窗口的 HWND。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var props = await file.GetBasicPropertiesAsync();
        var size = (long)props.Size;
        if (!global::App.Services.AttachmentSizePolicy.IsWithinLimit(size))
        {
            var sizeText = global::App.Services.AttachmentSizePolicy.FormatSize(size);
            var maxSizeText = global::App.Services.AttachmentSizePolicy.FormatSize(global::App.Services.AttachmentSizePolicy.MaxBytes);
            ViewModel.OperationError = $"文件 {sizeText} 超过附件上限 {maxSizeText}。";
            return;
        }

        // 文件读取 + 上传:ReadBufferAsync 在 code-behind 内、不在 VM 的 try/catch 内,
        // 文件被锁/网络盘掉线/拒绝访问会抛异常并从 async void 逸出——就地捕获并经 InfoBar 上报。
        try
        {
            var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
            await ViewModel.AddAttachmentAsync(item.Id, buffer.ToArray(), file.Name);
        }
        catch (Exception ex)
        {
            ViewModel.OperationError = $"读取文件失败:{ex.Message}";
        }
    }

    private async void OnDownloadAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item
            || (sender as FrameworkElement)?.DataContext is not AttachmentItem attachment)
            return;

        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
            SuggestedFileName = attachment.FileName,
        };
        picker.FileTypeChoices.Add("文件", new System.Collections.Generic.List<string> { "." });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
            return;

        var bytes = await ViewModel.DownloadAttachmentAsync(item.Id, attachment.Id);
        if (bytes is null)
            return;

        // 文件写入在 code-behind 内、不在 VM 的 try/catch 内:磁盘满/拒绝访问/网络盘掉线会抛异常
        // 并从 async void 逸出——就地捕获并经 InfoBar 上报。
        try
        {
            await Windows.Storage.FileIO.WriteBytesAsync(file, bytes);
        }
        catch (Exception ex)
        {
            ViewModel.OperationError = $"保存文件失败:{ex.Message}";
        }
    }

    private async void OnDeleteAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item
            || (sender as FrameworkElement)?.DataContext is not AttachmentItem attachment)
            return;

        if (await ConfirmAsync("删除附件", $"确定要删除附件“{attachment.FileName}”吗?此操作无法撤销。", "删除"))
            await ViewModel.DeleteAttachmentAsync(item.Id, attachment.Id);
    }

    // ── 筛选栏:分面/排序/保存视图 ────────────────────────────────────────────

    private void OnSavedViewsFlyoutOpening(object sender, object e)
    {
        // 只清理上次动态插入的视图项(位于列表顶部,静态分隔线之前),保留末尾静态的
        // 分隔线 + “保存当前视图…”(声明在 XAML,带 Click=OnSaveCurrentViewClick)。
        while (SavedViewsFlyout.Items.Count > 0
               && !ReferenceEquals(SavedViewsFlyout.Items[0], SavedViewsStaticSeparator))
        {
            SavedViewsFlyout.Items.RemoveAt(0);
        }

        if (ViewModel.SavedViews.Count == 0)
        {
            SavedViewsFlyout.Items.Insert(0, new MenuFlyoutItem { Text = "暂无已保存视图", IsEnabled = false });
            return;
        }

        var insertAt = 0;
        foreach (var view in ViewModel.SavedViews)
        {
            var applyItem = new MenuFlyoutItem { Text = view.Name };
            applyItem.Click += (_, _) => ViewModel.ApplyViewCommand.Execute(view);

            var deleteItem = new MenuFlyoutItem { Text = $"删除“{view.Name}”" };
            deleteItem.Click += (_, _) => ViewModel.DeleteViewCommand.Execute(view.Name);

            SavedViewsFlyout.Items.Insert(insertAt++, applyItem);
            SavedViewsFlyout.Items.Insert(insertAt++, deleteItem);
        }
    }

    private async void OnSaveCurrentViewClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "视图名称" };
        AutomationProperties.SetAutomationId(input, "VaultSaveViewNameTextBox");

        var dialog = new ContentDialog
        {
            Title = "保存当前视图",
            Content = input,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var name = input.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            ViewModel.SaveCurrentViewCommand.Execute(name);
    }

    private void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        ViewModel.FacetTotp = false;
        ViewModel.FacetAttachment = false;
        ViewModel.FacetUri = false;
        ViewModel.FacetFavoriteOnly = false;
        ViewModel.SearchText = string.Empty;
    }
}
