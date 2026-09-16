using System;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using App.Controls;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Models;
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
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Grid, RowActionState> _rowActionStates = new();
    private string? _currentLayoutState;
    private CipherListItem? _pendingListFocusItem;
    private bool _restoreListFocus;
    private bool _isRebindingSelection;
    private bool _subscriptionsAttached;
    private bool _restoringWorkspacePosition;
    private ScrollViewer? _listScrollViewer;
    private bool _hasEnteredWorkspace;
    private bool _syncingEditorTypeSelection;

    // 响应式布局阈值:>= 800 DIP 显示双栏(列表+详情);更窄时按是否有详情/编辑态在
    // NarrowList / NarrowDetail 之间切换,窄详情态显示"返回列表"入口。
    private const double VaultWideThreshold = 800;

    public VaultPage()
    {
        // 用完全限定名避免与命名空间 App.Services 冲突(App 类的静态属性 Services)。
        ViewModel = global::App.App.Services.GetRequiredService<VaultViewModel>();
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        AttachSubscriptions();
        Loaded += OnWorkspaceLoaded;
        Unloaded += OnWorkspaceUnloaded;
        UpdateDetailTemplate();
    }

    private void AttachSubscriptions()
    {
        if (_subscriptionsAttached)
            return;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FoldersChanged += OnVaultFoldersChanged;
        ViewModel.SelectionRebindRequested += OnSelectionRebindRequested;
        _subscriptionsAttached = true;
    }

    private void OnWorkspaceLoaded(object sender, RoutedEventArgs e)
    {
        AttachSubscriptions();
        ViewModel.RefreshEditorBindings(Bindings.Update);
        _listScrollViewer = FindDescendant<ScrollViewer>(CipherList);
        if (_listScrollViewer is not null)
            _listScrollViewer.ViewChanged += OnListViewChanged;
        QueueRestoreWorkspacePosition();
    }

    private void OnWorkspaceUnloaded(object sender, RoutedEventArgs e)
    {
        if (_listScrollViewer is not null)
            _listScrollViewer.ViewChanged -= OnListViewChanged;
        _listScrollViewer = null;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.FoldersChanged -= OnVaultFoldersChanged;
        ViewModel.SelectionRebindRequested -= OnSelectionRebindRequested;
        _subscriptionsAttached = false;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } descendant)
                return descendant;
        }
        return null;
    }

    private void OnListViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && !_restoringWorkspacePosition && _currentLayoutState != "NarrowDetail")
            RememberWorkspaceContext();
    }

    public void RememberWorkspaceContext()
    {
        if (_restoringWorkspacePosition || ViewModel.Filters.Count == 0)
            return;
        var focusedElement = XamlRoot is null ? null
            : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        string? focusedItemId = ViewModel.WorkspaceContext.FocusedItemId;
        var searchHadFocus = ViewModel.WorkspaceContext.SearchHadFocus;
        for (var current = focusedElement; current is not null;
             current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: CipherListItem item })
            {
                focusedItemId = item.Id;
                searchHadFocus = false;
                break;
            }
        }
        if (focusedElement is not null && IsDescendantOf(focusedElement, VaultSearchBox))
            searchHadFocus = true;
        ViewModel.RememberWorkspaceContext(
            _currentLayoutState == "NarrowDetail"
                ? ViewModel.WorkspaceContext.VerticalOffset
                : _listScrollViewer?.VerticalOffset ?? ViewModel.WorkspaceContext.VerticalOffset,
            focusedItemId,
            searchHadFocus);
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e) => RememberWorkspaceContext();

    private void OnSearchAcceleratorInvoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs e)
    {
        if (_currentLayoutState == "NarrowDetail")
        {
            if (ViewModel.IsEditing || !ViewModel.CanCancelEditor)
                return;
            ViewModel.ReturnToResults();
        }
        _ = VaultSearchBox.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null;
             current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }
        return false;
    }

    public void RestoreWorkspaceContext(string? tag = null)
    {
        ViewModel.RestoreWorkspaceContext(tag);
        QueueRestoreWorkspacePosition();
    }

    private void QueueRestoreWorkspacePosition()
    {
        var context = ViewModel.WorkspaceContext;
        var scope = ViewModel.SelectedFilterTag;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || ViewModel.Filters.Count == 0 || scope != ViewModel.SelectedFilterTag)
                return;
            _restoringWorkspacePosition = true;
            try
            {
                _listScrollViewer?.ChangeView(null, context.VerticalOffset, null, disableAnimation: true);
                if (ViewModel.Detail is not null || ViewModel.IsEditing)
                    return;
                if (context.SearchHadFocus)
                    _ = VaultSearchBox.Focus(FocusState.Programmatic);
                else if (context.FocusedItemId is { } id
                         && ViewModel.FilteredItems.FirstOrDefault(candidate => candidate.Id == id) is { } focusedItem
                         && CipherList.ContainerFromItem(focusedItem) is Control container)
                    _ = container.Focus(FocusState.Programmatic);
            }
            finally
            {
                _restoringWorkspacePosition = false;
            }
        });
    }

    private void OnVaultLayoutSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateVaultLayoutState();

    // 根据当前宽度与详情/编辑态计算并应用响应式布局状态。宽度变化不改变选择/机密,
    // 只有显式"返回列表"才清空选择(见 OnVaultNarrowBackClick),避免窄宽切换丢数据。
    private void UpdateVaultLayoutState()
    {
        var state = VaultLayoutRoot.ActualWidth >= VaultWideThreshold
            ? "Wide"
            : ViewModel.Detail is not null || ViewModel.IsEditing
                ? "NarrowDetail"
                : "NarrowList";
        var previousState = _currentLayoutState;
        _currentLayoutState = state;
        VaultNarrowBackButton.Visibility =
            state == "NarrowDetail" ? Visibility.Visible : Visibility.Collapsed;
        VisualStateManager.GoToState(this, state, true);

        if (previousState == "NarrowList" && state == "NarrowDetail")
            QueueNarrowDetailFocus();
        else if (_restoreListFocus && state == "NarrowList")
            QueueNarrowListFocus();
    }

    private void OnVaultNarrowBackClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanCancelEditor)
            return;

        _pendingListFocusItem = ViewModel.SelectedItem;
        _restoreListFocus = true;
        RememberWorkspaceContext();
        ViewModel.ReturnToResults();
        UpdateVaultLayoutState();
    }

    private void QueueNarrowDetailFocus()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentLayoutState == "NarrowDetail"
                && VaultNarrowBackButton.Visibility == Visibility.Visible)
            {
                _ = VaultNarrowBackButton.Focus(FocusState.Programmatic);
            }
        });
    }

    private void QueueNarrowListFocus()
    {
        var item = _pendingListFocusItem;
        _pendingListFocusItem = null;
        _restoreListFocus = false;

        if (item is not null)
            CipherList.ScrollIntoView(item);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentLayoutState != "NarrowList")
                return;

            if (item is not null && CipherList.ContainerFromItem(item) is Control container)
                _ = container.Focus(FocusState.Programmatic);
            else
                _ = CipherList.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>供 NotificationsHost 在收到 VaultChanged 推送后从 UI 线程调用，刷新列表快照。</summary>
    public void RefreshVaultList() => ViewModel.RefreshFromSnapshot();

    public static Visibility VisibleIfTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static ListViewSelectionMode SelectionModeFromBool(bool isSelectionMode) =>
        isSelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;

    // 批量操作按钮需同时满足"有选中项"与"当前无 Vault 操作占用门禁"才可点击。
    public static bool CanBatchOperate(bool hasSelection, bool canStartVaultOperation) =>
        hasSelection && canStartVaultOperation;

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
        if (!ViewModel.IsSelectionMode && !ViewModel.IsRebuildingItems && ViewModel.SelectedItem is { } selectedItem)
        {
            ViewModel.RememberWorkspaceContext(
                _listScrollViewer?.VerticalOffset ?? 0, selectedItem.Id, searchHadFocus: false);
            VaultDetailScroll.ChangeView(null, 0, null, disableAnimation: true);
        }
        if (!ViewModel.IsSelectionMode
            || ViewModel.IsRebuildingItems
            || _isRebindingSelection)
            return;
        foreach (var removed in e.RemovedItems)
            if (removed is CipherListItem item)
                ViewModel.SetItemSelected(item.Id, selected: false);
        foreach (var added in e.AddedItems)
            if (added is CipherListItem item)
                ViewModel.SetItemSelected(item.Id, selected: true);
    }

    private void OnSelectionRebindRequested(object? sender, EventArgs e)
    {
        if (!ViewModel.IsSelectionMode)
            return;

        _isRebindingSelection = true;
        try
        {
            CipherList.SelectedItems.Clear();
            foreach (var item in ViewModel.FilteredItems)
            {
                if (ViewModel.IsSelected(item.Id))
                    CipherList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _isRebindingSelection = false;
        }
    }

    private sealed class RowActionState
    {
        public bool IsPointerOver { get; set; }
        public bool IsFocusWithin { get; set; }
    }

    internal static bool ShouldShowRowActions(bool isPointerOver, bool isFocusWithin) =>
        isPointerOver || isFocusWithin;

    private void UpdateRowActionVisibility(
        object sender,
        bool? isPointerOver = null,
        bool? isFocusWithin = null)
    {
        if (sender is not Grid root
            || root.FindName("RowActions") is not FrameworkElement panel)
        {
            return;
        }

        var state = _rowActionStates.GetValue(root, static _ => new RowActionState());
        if (isPointerOver.HasValue)
            state.IsPointerOver = isPointerOver.Value;
        if (isFocusWithin.HasValue)
            state.IsFocusWithin = isFocusWithin.Value;

        var show = ShouldShowRowActions(state.IsPointerOver, state.IsFocusWithin);
        panel.Opacity = show ? 1 : 0;
        panel.IsHitTestVisible = show;
    }

    private static bool IsWithinRow(DependencyObject? element, Grid row)
    {
        for (var current = element; current is not null;
             current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, row))
                return true;
        }

        return false;
    }

    private void OnRowPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        UpdateRowActionVisibility(sender, isPointerOver: true);

    private void OnRowPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        UpdateRowActionVisibility(sender, isPointerOver: false);

    private void OnRowGotFocus(object sender, RoutedEventArgs e) =>
        UpdateRowActionVisibility(sender, isFocusWithin: true);

    private void OnRowLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid root)
            return;

        var focusedElement = root.XamlRoot is { } xamlRoot
            ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot) as DependencyObject
            : null;
        UpdateRowActionVisibility(root, isFocusWithin: IsWithinRow(focusedElement, root));
    }

    private void OnRowUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid root)
            return;

        _rowActionStates.Remove(root);
        if (root.FindName("RowActions") is FrameworkElement panel)
        {
            panel.Opacity = 0;
            panel.IsHitTestVisible = false;
        }
    }

    private static CipherListItem? RowItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as CipherListItem;

    private async void OnRowCopyPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is not { } item)
            return;
        await ViewModel.CopyPrimaryAsync(item.Id, PromptMasterPasswordAsync);
    }

    // 主密码重提示对话框:仅采集主密码并返回给条目级门禁校验(通过则门禁自行缓存授权);
    // 取消返回 null。经 Task 3 tracked 对话框——锁屏/登出会强制关闭并清空密码框。
    // 校验交由 IItemRepromptGate.AuthorizeAsync 完成,本方法不自持校验逻辑。
    private async Task<string?> PromptMasterPasswordAsync(CancellationToken ct)
    {
        var passwordBox = new PasswordBox { PlaceholderText = "主密码", MinWidth = 320 };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "该条目要求主密码重提示", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(passwordBox);
        var dialog = new ContentDialog
        {
            Title = "验证主密码",
            Content = panel,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        try
        {
            var result = await DialogHelper.ShowTrackedAsync(
                dialog,
                () => passwordBox.Password = string.Empty,
                ct);
            return result == ContentDialogResult.Primary ? passwordBox.Password : null;
        }
        finally
        {
            passwordBox.Password = string.Empty;
        }
    }

    // 详情级"验证后显示敏感字段"入口:经条目级门禁解锁当前详情的敏感投影。
    private async void OnUnlockSensitiveFieldsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;
        await ViewModel.UnlockDetailAsync(item.Id, PromptMasterPasswordAsync);
    }

    private async void OnCopyProtectedPasswordClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ViewModel.CopyPasswordAsync(item.Id, PromptMasterPasswordAsync);
    }

    private void OnRemoveCustomFieldClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CustomFieldEditorDraft field)
            ViewModel.RemoveCustomFieldCommand.Execute(field);
    }

    private async void OnRowEditClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
        {
            var authorized = await ViewModel.BeginEditAsync(item.Id, PromptMasterPasswordAsync);
            if (authorized.IsSuccess)
                SyncEditorTypeSelection();
        }
    }

    private async void OnRowCloneClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
        {
            var authorized = await ViewModel.BeginCloneAsync(item.Id, PromptMasterPasswordAsync);
            if (authorized.IsSuccess)
                SyncEditorTypeSelection();
        }
    }

    private async void OnRowFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await ViewModel.ToggleFavoriteAsync(item.Id);
    }

    private async void OnRowArchiveClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await ViewModel.ArchiveAsync(item.Id);
    }

    private async void OnRowUnarchiveClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await ViewModel.UnarchiveAsync(item.Id);
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
        AttachSubscriptions();
        if (_hasEnteredWorkspace)
            ViewModel.RefreshFromSnapshot();
        _hasEnteredWorkspace = true;
        RestoreWorkspaceContext(e.Parameter as string);
        UpdateVaultLayoutState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        RememberWorkspaceContext();
        base.OnNavigatedFrom(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultViewModel.Detail))
            UpdateDetailTemplate();
        if (e.PropertyName is nameof(VaultViewModel.Detail) or nameof(VaultViewModel.IsEditing))
            UpdateVaultLayoutState();
        if (e.PropertyName == nameof(VaultViewModel.WorkspaceContext))
        {
            if (ViewModel.Filters.Count == 0)
            {
                _pendingListFocusItem = null;
                _restoreListFocus = false;
            }
            QueueRestoreWorkspacePosition();
        }
    }

    private void OnAddWebsiteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanEditEditorDraft)
            ViewModel.EditorDraft?.Login.Uris.Add(new LoginUriEditorDraft());
    }

    private void OnRemoveWebsiteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CanEditEditorDraft
            && (sender as FrameworkElement)?.DataContext is LoginUriEditorDraft uri)
            ViewModel.EditorDraft?.Login.Uris.Remove(uri);
    }

    private void OnAddLoginClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Login);

    private void OnAddCardClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Card);

    private void OnAddIdentityClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Identity);

    private void OnAddNoteClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Note);

    private void OnAddSshClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Ssh);

    private void OnAddBankAccountClick(object sender, RoutedEventArgs e) =>
        BeginAdd(VaultItemKind.BankAccount);

    private void OnAddDriversLicenseClick(object sender, RoutedEventArgs e) =>
        BeginAdd(VaultItemKind.DriversLicense);

    private void OnAddPassportClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Passport);

    private void BeginAdd(VaultItemKind kind)
    {
        RememberWorkspaceContext();
        ViewModel.BeginAdd(kind);
        SyncEditorTypeSelection();
        VaultDetailScroll.ChangeView(null, 0, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && ViewModel.IsEditing)
                _ = CipherEditorNameBox.Focus(FocusState.Programmatic);
        });
    }

    private void OnCancelCipherEditorClick(object sender, RoutedEventArgs e) => ViewModel.CancelEdit();

    private async void OnSaveCipherEditorClick(object sender, RoutedEventArgs e)
    {
        if ((await ViewModel.SaveDraftAsync()).IsSuccess)
            UpdateDetailTemplate();
    }

    private async void OnAbandonPendingCipherCreateClick(object sender, RoutedEventArgs e)
    {
        var expected = ViewModel.CapturePendingCipherCreate();
        if (expected is null)
            return;

        Core.Session.VaultSessionLease lease;
        try
        {
            lease = global::App.App.Services
                .GetRequiredService<Vault.VaultSession>()
                .CaptureLease();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var risk = expected.CommitConfirmed
            ? "服务器已经返回成功，该条目很可能已经创建。"
            : "服务器仍可能已经创建该条目。";
        var duplicateRisk = ViewModel.HasRecoverablePendingDraft
            ? "解除防重复保护后再次保存当前草稿，可能创建重复条目。"
            : "解除防重复保护后重新创建同一条目，可能产生重复项。";
        var confirmed = await DialogHelper.ConfirmAsync(
            XamlRoot,
            lease,
            "放弃待确认状态？",
            $"{risk}此操作只会清除本机的待确认记录，不会删除服务器条目，也不会重新提交。{duplicateRisk}",
            "仍要放弃",
            "返回检查");
        if (!confirmed)
            return;

        await ViewModel.AbandonPendingCipherCreateAsync(expected);
    }

    private void OnCipherEditorTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingEditorTypeSelection || !ViewModel.IsEditing
            || CipherEditorTypeBox.SelectedItem is not ComboBoxItem item)
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
        var selected = CipherEditorTypeBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == tag);
        if (ReferenceEquals(CipherEditorTypeBox.SelectedItem, selected))
            return;
        _syncingEditorTypeSelection = true;
        try
        {
            CipherEditorTypeBox.SelectedItem = selected;
        }
        finally { _syncingEditorTypeSelection = false; }
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
            BankAccountDetail => "BankAccountTemplate",
            DriversLicenseDetail => "DriversLicenseTemplate",
            PassportDetail => "PassportTemplate",
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

        if (await DialogHelper.ShowTrackedAsync(dialog) == ContentDialogResult.Primary)
            await ViewModel.PermanentDeleteSelectedCommand.ExecuteAsync(null);
    }

    private void OnVaultFoldersChanged(object? sender, EventArgs e) =>
        global::App.App.MainWindow?.RefreshFolderNavigation();

    private async void OnEditCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
        {
            var authorized = await ViewModel.BeginEditAsync(item.Id, PromptMasterPasswordAsync);
            if (authorized.IsSuccess)
                SyncEditorTypeSelection();
        }
    }

    private async void OnTransferCipherToOrganizationClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { CanTransferToOrganization: true } item)
            return;

        CipherPlacementBaseline baseline;
        try
        {
            baseline = ViewModel.GetCipherPlacementBaseline(item.Id);
        }
        catch (InvalidOperationException)
        {
            ViewModel.OperationError = "条目已发生变化，请刷新后重试。";
            return;
        }

        var result = await PromptOrganizationTransferAsync();
        if (result is null)
            return;
        await ViewModel.TransferCipherToOrganizationAsync(
            baseline,
            result.Destination.OrganizationId!,
            result.CollectionIds);
    }

    private async void OnManageCipherCollectionsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { CanManageCollections: true } item
            || string.IsNullOrWhiteSpace(item.OrganizationId))
        {
            return;
        }

        CipherPlacementBaseline baseline;
        try
        {
            baseline = ViewModel.GetCipherPlacementBaseline(item.Id);
        }
        catch (InvalidOperationException)
        {
            ViewModel.OperationError = "条目已发生变化，请刷新后重试。";
            return;
        }

        var options = ViewModel.GetCipherCollectionOptions(
            item.OrganizationId,
            baseline.CollectionIds,
            lockReadOnlyAssignments: true);
        var collectionIds = await PromptCollectionSelectionAsync(
            "管理集合",
            "选择此条目所属的集合。只读集合会保留且不能取消。",
            options);
        if (collectionIds is null)
            return;
        await ViewModel.SetCipherCollectionsAsync(baseline, collectionIds);
    }

    private async void OnTransferSelectedToOrganizationClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanTransferSelectionToOrganization)
        {
            ViewModel.OperationError = string.IsNullOrEmpty(ViewModel.SelectionPlacementError)
                ? "所选条目不能批量转移到组织。"
                : ViewModel.SelectionPlacementError;
            return;
        }

        var result = await PromptOrganizationTransferAsync(
            "批量转移到组织",
            $"将 {ViewModel.SelectedCount} 个个人条目转移到同一组织。请选择至少一个集合。",
            "转移所选");
        if (result is null)
            return;

        await ViewModel.TransferSelectedCiphersToOrganizationAsync(
            result.Destination.OrganizationId!,
            result.CollectionIds);
    }

    private async void OnAddSelectedToCollectionsClick(object sender, RoutedEventArgs e) =>
        await PromptBulkCollectionUpdateAsync(removeCollections: false);

    private async void OnRemoveSelectedFromCollectionsClick(object sender, RoutedEventArgs e) =>
        await PromptBulkCollectionUpdateAsync(removeCollections: true);

    private async Task PromptBulkCollectionUpdateAsync(bool removeCollections)
    {
        if (!ViewModel.CanManageSelectionCollections
            || ViewModel.SelectedOrganizationId is not { } organizationId)
        {
            ViewModel.OperationError = string.IsNullOrEmpty(ViewModel.SelectionPlacementError)
                ? "所选条目不能批量更改集合。"
                : ViewModel.SelectionPlacementError;
            return;
        }

        var options = ViewModel.GetCipherCollectionOptions(organizationId);
        if (options.Count == 0)
        {
            ViewModel.OperationError = "此组织没有可写集合。";
            return;
        }

        var organizationName = ViewModel.Filters.FirstOrDefault(filter =>
            filter.Kind == FilterKind.Organization
            && string.Equals(filter.OrganizationId, organizationId, StringComparison.Ordinal))?.Label
            ?? organizationId;
        var action = removeCollections ? "移出" : "加入";
        var collectionIds = await PromptCollectionSelectionAsync(
            $"批量{action}集合",
            $"组织：{organizationName}\n选择要将 {ViewModel.SelectedCount} 个条目{action}的集合。",
            options,
            primaryButtonText: action,
            requireSelection: true);
        if (collectionIds is null)
            return;

        if (removeCollections)
            await ViewModel.RemoveSelectedCiphersFromCollectionsAsync(collectionIds);
        else
            await ViewModel.AddSelectedCiphersToCollectionsAsync(collectionIds);
    }

    private async Task<OrganizationTransferDialogResult?> PromptOrganizationTransferAsync(
        string title = "转移到组织",
        string? message = null,
        string primaryButtonText = "转移")
    {
        var destinations = ViewModel.GetOrganizationDestinations();
        if (destinations.Count == 0)
        {
            ViewModel.OperationError = "当前账户没有可写入的组织集合。";
            return null;
        }

        var organizationBox = new ComboBox
        {
            Header = "组织",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var candidate in destinations)
        {
            organizationBox.Items.Add(new ComboBoxItem
            {
                Content = candidate.DisplayName,
                Tag = candidate,
            });
        }

        var collectionsPanel = new StackPanel { Spacing = 4 };
        var collectionBoxes = new List<(string Id, CheckBox Box)>();
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = message ?? "转移后条目将由组织管理。请选择至少一个集合。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(organizationBox);
        content.Children.Add(new TextBlock { Text = "集合" });
        content.Children.Add(new ScrollViewer { MaxHeight = 280, Content = collectionsPanel });

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };

        void UpdatePrimaryButton() => dialog.IsPrimaryButtonEnabled =
            collectionBoxes.Any(entry => entry.Box.IsChecked == true);

        void ReloadCollections()
        {
            collectionsPanel.Children.Clear();
            collectionBoxes.Clear();
            if (organizationBox.SelectedItem is not ComboBoxItem
                {
                    Tag: CipherDestinationOption { OrganizationId: { } organizationId },
                })
            {
                UpdatePrimaryButton();
                return;
            }

            foreach (var option in ViewModel.GetCipherCollectionOptions(organizationId))
            {
                var box = new CheckBox
                {
                    Content = option.Name,
                    IsChecked = option.IsSelected,
                    IsEnabled = option.CanSelect,
                };
                box.Checked += (_, _) => UpdatePrimaryButton();
                box.Unchecked += (_, _) => UpdatePrimaryButton();
                collectionBoxes.Add((option.Id, box));
                collectionsPanel.Children.Add(box);
            }
            UpdatePrimaryButton();
        }

        organizationBox.SelectionChanged += (_, _) => ReloadCollections();
        organizationBox.SelectedIndex = 0;
        if (await DialogHelper.ShowTrackedAsync(dialog) != ContentDialogResult.Primary
            || organizationBox.SelectedItem is not ComboBoxItem
            {
                Tag: CipherDestinationOption destination,
            })
        {
            return null;
        }

        return new OrganizationTransferDialogResult(
            destination,
            collectionBoxes
                .Where(entry => entry.Box.IsChecked == true)
                .Select(entry => entry.Id)
                .ToList());
    }

    private async Task<IReadOnlyList<string>?> PromptCollectionSelectionAsync(
        string title,
        string message,
        IReadOnlyList<CipherCollectionOption> options,
        string primaryButtonText = "保存",
        bool requireSelection = false)
    {
        var boxes = new List<(string Id, CheckBox Box)>();
        var collectionsPanel = new StackPanel { Spacing = 4 };
        foreach (var option in options)
        {
            var box = new CheckBox
            {
                Content = option.Name,
                IsChecked = option.IsSelected,
                IsEnabled = option.CanSelect,
            };
            boxes.Add((option.Id, box));
            collectionsPanel.Children.Add(box);
        }

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new ScrollViewer { MaxHeight = 280, Content = collectionsPanel });
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        void UpdatePrimaryButton() => dialog.IsPrimaryButtonEnabled =
            !requireSelection || boxes.Any(entry => entry.Box.IsChecked == true);
        foreach (var entry in boxes)
        {
            entry.Box.Checked += (_, _) => UpdatePrimaryButton();
            entry.Box.Unchecked += (_, _) => UpdatePrimaryButton();
        }
        UpdatePrimaryButton();

        if (await DialogHelper.ShowTrackedAsync(dialog) != ContentDialogResult.Primary)
            return null;
        return boxes
            .Where(entry => entry.Box.IsChecked == true)
            .Select(entry => entry.Id)
            .ToList();
    }

    private async void OnDeleteCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;
        if (await ConfirmAsync("移到回收站", $"确定要将“{item.Name}”移到回收站吗?", "删除"))
            await ViewModel.SoftDeleteAsync(item.Id);
    }

    private async void OnArchiveCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ViewModel.ArchiveAsync(item.Id);
    }

    private async void OnUnarchiveCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ViewModel.UnarchiveAsync(item.Id);
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
        return await DialogHelper.ShowTrackedAsync(dialog) == ContentDialogResult.Primary ? input.Text : null;
    }

    private async void OnAddAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await FilePickerHelper.PickSingleAsync(picker);
            if (file is null)
                return;

            await FilePickerHelper.RunTrackedAsync<object?>(
                async ct =>
                {
                    var source = await global::App.Services.StorageFileSource.CreateAsync(
                        file,
                        Core.Services.AttachmentLimits.MaxPlaintextBytes,
                        ct);
                    await ViewModel.AddAttachmentAsync(item.Id, source, ct);
                    return null;
                });
        }
        catch (ArgumentOutOfRangeException)
        {
            var maxSizeText = global::App.Services.AttachmentSizePolicy.FormatSize(
                global::App.Services.AttachmentSizePolicy.MaxBytes);
            ViewModel.OperationError = $"文件超过附件上限 {maxSizeText}。";
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex))
        {
        }
        catch (Exception ex) when (IsRecoverableFileTransferException(ex))
        {
            ViewModel.OperationError = FileTransferError(ex, "上传附件失败");
        }
    }

    private async void OnDownloadAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item
            || (sender as FrameworkElement)?.DataContext is not AttachmentItem attachment)
            return;
        if (!TryCaptureExpectedLease(out var expectedLease))
            return;

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                SuggestedFileName = attachment.FileName,
            };
            picker.FileTypeChoices.Add("文件", new System.Collections.Generic.List<string> { "." });
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await FilePickerHelper.PickSaveAsync(picker);
            if (file is null)
                return;

            await FilePickerHelper.RunTrackedAsync<object?>(
                async ct =>
                {
                    using var transaction = await file.OpenTransactedWriteAsync().AsTask(ct);
                    await using var destination = transaction.Stream.AsStreamForWrite();
                    destination.SetLength(0);
                    destination.Position = 0;

                    var download = await ViewModel.DownloadAttachmentAsync(
                        item.Id,
                        attachment.Id,
                        PromptMasterPasswordAsync,
                        destination,
                        ct);
                    if (!download.IsSuccess)
                        return null;

                    await destination.FlushAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    await FilePickerHelper.CommitSensitiveAsync(
                        transaction,
                        expectedLease,
                        ct);
                    return null;
                });
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex))
        {
        }
        catch (Exception ex) when (IsRecoverableFileTransferException(ex))
        {
            ViewModel.OperationError = FileTransferError(ex, "保存附件失败");
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

    private static string FileTransferError(Exception exception, string fallback)
    {
        if (Api.RequestTimeoutClassifier.IsTimeout(exception))
            return "文件传输长时间没有进展，请检查连接后重试。";

        return exception switch
        {
            UnauthorizedAccessException => "没有访问所选文件的权限。",
            InvalidDataException or System.Security.Cryptography.CryptographicException =>
                "附件完整性校验失败，文件未保存。",
            HttpRequestException => "网络请求失败，请检查连接后重试。",
            IOException => "文件读写失败，请检查磁盘和文件状态后重试。",
            ObjectDisposedException => "操作已取消，文件未保存。",
            _ => fallback,
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

        if (await DialogHelper.ShowTrackedAsync(dialog) != ContentDialogResult.Primary)
            return;

        var name = input.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            ViewModel.SaveCurrentViewCommand.Execute(name);
    }

    private void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedTypeIndex = 0;
        ViewModel.FacetTotp = false;
        ViewModel.FacetAttachment = false;
        ViewModel.FacetUri = false;
        ViewModel.FacetFavoriteOnly = false;
        ViewModel.SearchText = string.Empty;
    }

    private sealed record OrganizationTransferDialogResult(
        CipherDestinationOption Destination,
        IReadOnlyList<string> CollectionIds);
}
