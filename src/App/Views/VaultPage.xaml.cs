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
    private bool _confirmingLeave;
    private bool _sensitiveActionPending;
    private int _selectionRequestVersion;
    private Func<Task<UiOperationResult>>? _undoAction;
    private DispatcherTimer? _toastTimer;
    private Button? _copiedRowButton;
    private string? _copiedRowGlyph;
    private long _copiedRowVersion;
    private HealthReport? _detailHealthReport;

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
        KeyDown += OnVaultKeyDown;
        ActualThemeChanged += (_, _) =>
        {
            RefreshRowVisuals();
            UpdateDetailStatus();
            if (CipherList is not null)
                foreach (var icon in Descendants<FontIcon>(CipherList).Where(icon => icon.Name == "RowIssueGlyph"))
                    UpdateRowIssue(icon);
        };
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
        UpdateVaultLayoutState();
        if (ViewModel.AutoSelectFirstResult && !ViewModel.IsEditing && ViewModel.SelectedItem is null)
            ViewModel.SelectedItem = ViewModel.FilteredItems.FirstOrDefault();
        ViewModel.RefreshEditorBindings(Bindings.Update);
        _listScrollViewer = FindDescendant<ScrollViewer>(CipherList);
        if (_listScrollViewer is not null)
            _listScrollViewer.ViewChanged += OnListViewChanged;
        QueueRestoreWorkspacePosition();
        if (global::App.App.Services.GetService<IClipboardService>() is IClipboardStatus clipboard)
            clipboard.Changed += OnClipboardChanged;
        global::App.App.Services.GetRequiredService<SecurityReportViewModel>().PropertyChanged += OnSecurityReportChanged;
        _ = RefreshDetailSecurityAsync();
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
        if (global::App.App.Services.GetService<IClipboardService>() is IClipboardStatus clipboard)
            clipboard.Changed -= OnClipboardChanged;
        _toastTimer?.Stop();
        _undoAction = null;
        VaultToast.Visibility = Visibility.Collapsed;
        VaultUndoButton.Visibility = Visibility.Collapsed;
        ResetRowCopyFeedback();
        _detailHealthReport = null;
        global::App.App.Services.GetRequiredService<SecurityReportViewModel>().PropertyChanged -= OnSecurityReportChanged;
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
        FocusSearch();
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
                    FocusSearch();
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
        var windowWidth = XamlRoot?.Size.Width ?? VaultLayoutRoot.ActualWidth;
        ViewModel.AutoSelectFirstResult = windowWidth >= VaultWideThreshold;
        var state = windowWidth >= VaultWideThreshold
            ? "Wide"
            : ViewModel.Detail is not null || ViewModel.IsEditing
                ? "NarrowDetail"
                : "NarrowList";
        var previousState = _currentLayoutState;
        _currentLayoutState = state;
        VaultNarrowBackButton.Visibility =
            state == "NarrowDetail" ? Visibility.Visible : Visibility.Collapsed;
        VisualStateManager.GoToState(this, state, true);
        if (state == "Wide")
            VaultListColumn.Width = new GridLength(windowWidth >= 1680 ? 380 : windowWidth < 1000 ? 300 : 344);

        if (previousState == "NarrowList" && state == "NarrowDetail")
            QueueNarrowDetailFocus();
        else if (_restoreListFocus && state == "NarrowList")
            QueueNarrowListFocus();
    }

    private async void OnVaultNarrowBackClick(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeaveEditorAsync())
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
    public void RefreshVaultList()
    {
        ViewModel.RefreshFromSnapshot();
        if (IsLoaded)
            _ = RefreshDetailSecurityAsync();
    }

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
    private async void OnCipherSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.IsSelectionMode && !ViewModel.IsRebuildingItems && !_isRebindingSelection)
        {
            var requested = CipherList.SelectedItem as CipherListItem;
            if (!ReferenceEquals(requested, ViewModel.SelectedItem))
            {
                var requestVersion = ++_selectionRequestVersion;
                var previous = ViewModel.SelectedItem;
                if (ViewModel.IsEditing && !await ConfirmLeaveEditorAsync())
                {
                    _isRebindingSelection = true;
                    try { CipherList.SelectedItem = previous; }
                    finally { _isRebindingSelection = false; }
                    return;
                }
                if (requestVersion != _selectionRequestVersion)
                    return;
                ViewModel.SelectedItem = requested;
            }
        }
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
        RefreshRowVisuals();
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
        show &= root.DataContext is CipherListItem { IsDeleted: false };
        panel.Opacity = show ? 1 : 0;
        panel.IsHitTestVisible = show;
        if (root.FindName("RowStatusIcons") is FrameworkElement status)
            status.Opacity = show ? 0 : 1;
        UpdateRowVisual(root, state.IsPointerOver);
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
        var result = item.Kind == VaultItemKind.Identity
            ? await ViewModel.CopyFieldAsync(item.Id, "email", PromptMasterPasswordAsync)
            : await ViewModel.CopyPrimaryAsync(item.Id, PromptMasterPasswordAsync);
        ShowRowCopyResult(sender, result);
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
            XamlRoot = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot,
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
        if (await VerifySensitiveActionAsync("显示敏感字段"))
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
            if (!await ConfirmLeaveEditorAsync() || !await VerifySensitiveActionAsync("编辑条目"))
                return;
            var authorized = await ViewModel.BeginEditAsync(item.Id, PromptMasterPasswordAsync);
            if (authorized.IsSuccess)
                SyncEditorTypeSelection();
        }
    }

    private async void OnRowCloneClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
        {
            if (!await ConfirmLeaveEditorAsync() || !await VerifySensitiveActionAsync("创建条目副本"))
                return;
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
            await ArchiveWithUndoAsync(item);
    }

    private async void OnRowUnarchiveClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await UnarchiveWithUndoAsync(item);
    }

    private async void OnRowSoftDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await DeleteWithUndoAsync(item);
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

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AttachSubscriptions();
        if (_hasEnteredWorkspace)
            ViewModel.RefreshFromSnapshot();
        _hasEnteredWorkspace = true;
        var route = e.Parameter as string;
        if (route == "vault:new-generated")
        {
            if (await ConfirmLeaveEditorAsync())
            {
                RestoreWorkspaceContext("vault:allitems");
                await BeginNewLoginAsync(global::App.App.Services.GetRequiredService<GeneratorViewModel>().GeneratedPassword);
            }
        }
        else if (route?.StartsWith("vault:change-password:", StringComparison.Ordinal) == true)
        {
            if (await ConfirmLeaveEditorAsync())
            {
                RestoreWorkspaceContext("vault:item:" + route["vault:change-password:".Length..]);
                await ChangePasswordAsync(route["vault:change-password:".Length..]);
            }
        }
        else if (route?.StartsWith("vault:edit:", StringComparison.Ordinal) == true)
        {
            var itemId = route["vault:edit:".Length..];
            if (await ConfirmLeaveEditorAsync())
            {
                RestoreWorkspaceContext("vault:item:" + itemId);
                if (await VerifySensitiveActionAsync("编辑条目")
                    && (await ViewModel.BeginEditAsync(itemId, PromptMasterPasswordAsync)).IsSuccess)
                    SyncEditorTypeSelection();
            }
        }
        else
            RestoreWorkspaceContext(route);
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
        {
            UpdateDetailTemplate();
            RefreshRowVisuals();
            UpdateDetailStatus();
            AnimateDetail();
            foreach (var field in Descendants<FieldRow>(this))
                BindFieldAuthorization(field);
        }
        if (e.PropertyName == nameof(VaultViewModel.SearchText))
            foreach (var block in Descendants<TextBlock>(CipherList).Where(block => block.Name == "RowNameText"))
                ApplySearchHighlight(block);
        if (e.PropertyName is nameof(VaultViewModel.IsSelectionMode) or nameof(VaultViewModel.SelectedCount))
            RefreshRowVisuals();
        if (e.PropertyName is nameof(VaultViewModel.Detail) or nameof(VaultViewModel.IsEditing))
            UpdateVaultLayoutState();
        if (e.PropertyName == nameof(VaultViewModel.EditorDraft))
            LoginPasswordEditor.PasswordRevealMode = PasswordRevealMode.Peek;
        if (e.PropertyName == nameof(VaultViewModel.IsEditing) && !ViewModel.IsEditing)
        {
            LoginPasswordEditor.PasswordRevealMode = PasswordRevealMode.Peek;
            GeneratedPasswordHint.Visibility = Visibility.Collapsed;
        }
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

    private async void BeginAdd(VaultItemKind kind)
    {
        if (!await ConfirmLeaveEditorAsync())
            return;
        BeginNewItemCore(kind);
    }

    private void BeginNewItemCore(VaultItemKind kind)
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
        var name = ViewModel.EditorDraft?.Name.Trim();
        var isNew = ViewModel.CanChangeEditorType;
        var passwordChanged = ViewModel.HasEditorPasswordChange;
        var result = await ViewModel.SaveDraftAsync();
        if (result.IsSuccess)
        {
            if (passwordChanged && !string.IsNullOrEmpty(result.Value))
            {
                _detailHealthReport = null;
                global::App.App.Services.GetRequiredService<SecurityReportViewModel>().MarkPasswordChanged(result.Value);
                _ = RefreshDetailSecurityAsync();
            }
            UpdateDetailTemplate();
            ShowToast(isNew ? $"已创建“{name}”" : $"已保存“{name}”");
        }
        else if (string.IsNullOrWhiteSpace(ViewModel.EditorDraft?.Name))
            CipherEditorNameBox.Focus(FocusState.Programmatic);
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
            if (!await ConfirmLeaveEditorAsync() || !await VerifySensitiveActionAsync("编辑条目"))
                return;
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
        await DeleteWithUndoAsync(item);
    }

    private async void OnArchiveCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ArchiveWithUndoAsync(item);
    }

    private async void OnUnarchiveCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await UnarchiveWithUndoAsync(item);
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

    public void ApplySearch(string text) => ViewModel.SearchText = text ?? string.Empty;

    public void FocusSearch() => global::App.App.MainWindow?.FocusVaultSearch();

    public void BeginNewItem(VaultItemKind kind = VaultItemKind.Login) => BeginAdd(kind);

    public async Task BeginNewLoginAsync(string password)
    {
        if (!await ConfirmLeaveEditorAsync())
            return;
        BeginNewItemCore(VaultItemKind.Login);
        if (ViewModel.EditorDraft is not { } draft)
            return;
        draft.Login.Password = password;
        LoginPasswordEditor.PasswordRevealMode = PasswordRevealMode.Visible;
    }

    public async Task ChangePasswordAsync(string itemId, string? generatedPassword = null)
    {
        if (!await ConfirmLeaveEditorAsync() || !await VerifySensitiveActionAsync("更换密码"))
            return;
        var result = await ViewModel.BeginEditAsync(itemId, PromptMasterPasswordAsync);
        if (!result.IsSuccess || ViewModel.EditorDraft is not { IsLogin: true } draft)
            return;
        draft.Login.Password = generatedPassword
            ?? global::App.App.Services.GetRequiredService<GeneratorViewModel>().GenerateCredentialForLogin();
        SyncEditorTypeSelection();
        LoginPasswordEditor.PasswordRevealMode = PasswordRevealMode.Visible;
        GeneratedPasswordHint.Visibility = Visibility.Visible;
    }

    public async Task<bool> ConfirmLeaveEditorAsync()
    {
        if (!ViewModel.IsEditing)
            return true;
        if (!ViewModel.CanCancelEditor || _confirmingLeave)
            return false;
        if (!ViewModel.HasUnsavedEditorChanges)
        {
            ViewModel.CancelEdit();
            return true;
        }
        _confirmingLeave = true;
        try
        {
            if (!await ConfirmAsync("放弃未保存的更改？", "你所做的更改尚未保存。", "放弃更改"))
                return false;
            ViewModel.CancelEdit();
            return true;
        }
        finally { _confirmingLeave = false; }
    }

    public bool MoveSearchSelection(int direction)
    {
        if (ViewModel.IsEditing || ViewModel.FilteredItems.Count == 0 || _sensitiveActionPending)
            return false;
        var currentIndex = ViewModel.SelectedItem is { } selected ? ViewModel.FilteredItems.IndexOf(selected) : -1;
        var nextIndex = Math.Clamp(currentIndex + direction, 0, ViewModel.FilteredItems.Count - 1);
        ViewModel.SelectedItem = ViewModel.FilteredItems[nextIndex];
        CipherList.ScrollIntoView(ViewModel.SelectedItem);
        return true;
    }

    public void AcceptSearchResult()
    {
        if (ViewModel.IsEditing || ViewModel.FilteredItems.Count == 0)
            return;
        ViewModel.SelectedItem ??= ViewModel.FilteredItems[0];
        CipherList.ScrollIntoView(ViewModel.SelectedItem);
        if (CipherList.ContainerFromItem(ViewModel.SelectedItem) is Control row)
            row.Focus(FocusState.Keyboard);
        else
            CipherList.Focus(FocusState.Keyboard);
    }

    public static string FavoriteGlyph(bool favorite) => favorite ? "\uE735" : "\uE734";

    public static Visibility VisibleForLogin(VaultItemKind kind) =>
        kind == VaultItemKind.Login ? Visibility.Visible : Visibility.Collapsed;

    public static string PrimaryCopyGlyph(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login or VaultItemKind.Ssh => "\uE72E",
        VaultItemKind.Card => "\uE8C7",
        VaultItemKind.Identity => "\uE715",
        _ => "\uE8C8",
    };

    private async Task<bool> VerifySensitiveActionAsync(string message, CancellationToken ct = default)
    {
        if (_sensitiveActionPending)
            return false;
        _sensitiveActionPending = true;
        try
        {
            if (global::App.App.MainWindow is not { } window)
                return false;
            var verifier = global::App.App.Services.GetRequiredService<ISensitiveActionVerificationService>();
            return await verifier.VerifyAsync(WinRT.Interop.WindowNative.GetWindowHandle(window), message, PromptMasterPasswordAsync, ct);
        }
        catch (OperationCanceledException exception) when (UiExceptionPolicy.IsGenuineCancellation(exception, ct)) { return false; }
        catch (OperationCanceledException exception) when (Api.RequestTimeoutClassifier.IsTimeout(exception))
        {
            ViewModel.OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return false;
        }
        finally { _sensitiveActionPending = false; }
    }

    private void OnCopyStripLoaded(object sender, RoutedEventArgs e)
    {
        DetailCopyStrip.AuthorizeSensitiveActionAsync = async (_, reveal, ct) =>
        {
            var id = ViewModel.Detail?.Id;
            if (id is null || ViewModel.Detail?.IsDeleted != false)
                return false;
            if (!reveal)
                return await ViewModel.AuthorizeFieldCopyAsync(id, PromptMasterPasswordAsync, ct);
            if (reveal && !await VerifySensitiveActionAsync("显示敏感字段", ct))
                return false;
            if (ViewModel.Detail?.Id != id)
                return false;
            if (ViewModel.Detail.SensitiveUnlocked)
                return true;
            var authorized = await ViewModel.UnlockDetailAsync(id, PromptMasterPasswordAsync, ct);
            return authorized.IsSuccess && ViewModel.Detail?.Id == id;
        };
        DetailCopyStrip.ReadAuthorizedDetail = () => ViewModel.Detail is { } detail
            ? ViewModel.ReadAuthorizedDetail(detail.Id)
            : null;
    }

    private void OnCopyStripCopied(object? sender, CopyStripCopiedEventArgs e)
    {
        ViewModel.RecordRecentUse(e.ItemId);
        UpdateDetailStatus();
    }

    private async void OnOpenWebsiteClick(object sender, RoutedEventArgs e)
    {
        var id = ViewModel.Detail?.Id;
        if (id is null || !Uri.TryCreate(ViewModel.DetailWebsite, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try
        {
            if (await Windows.System.Launcher.LaunchUriAsync(uri))
            {
                ViewModel.RecordRecentUse(id);
                UpdateDetailStatus();
                if (ViewModel.Detail?.Id == id)
                    DetailCopyStrip.BeginRelay();
            }
            else
                ViewModel.OperationError = "无法打开网站。请检查默认浏览器设置。";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            ViewModel.OperationError = "无法打开网站。请稍后重试。";
        }
    }

    private async void OnDetailFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ViewModel.ToggleFavoriteAsync(item.Id);
    }

    private async void OnDetailCloneClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item || !await VerifySensitiveActionAsync("创建条目副本"))
            return;
        if ((await ViewModel.BeginCloneAsync(item.Id, PromptMasterPasswordAsync)).IsSuccess)
            SyncEditorTypeSelection();
    }

    private void OnDetailHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 520;
        Grid.SetColumn(DetailHeaderActions, compact ? 1 : 2);
        Grid.SetRow(DetailHeaderActions, compact ? 1 : 0);
        Grid.SetColumnSpan(DetailHeaderActions, compact ? 2 : 1);
        OpenWebsiteLabel.Visibility = e.NewSize.Width < 440 ? Visibility.Collapsed : Visibility.Visible;
        DetailStatusText.MaxWidth = Math.Max(100, e.NewSize.Width - 240);
    }

    private void OnDetailScrollSizeChanged(object sender, SizeChangedEventArgs e) =>
        DetailBodyPanel.Width = Math.Min(920, Math.Max(0, e.NewSize.Width - 80));

    private void OnEditorHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 520;
        Grid.SetRow(EditorHeaderActions, compact ? 1 : 0);
        Grid.SetColumn(EditorHeaderActions, compact ? 1 : 2);
        Grid.SetColumnSpan(EditorHeaderActions, compact ? 2 : 1);
    }

    public static Visibility NewDraftVisibility(bool editing, bool isNew) =>
        editing && isNew ? Visibility.Visible : Visibility.Collapsed;

    public static string DraftName(string? name, VaultItemKind kind) => !string.IsNullOrWhiteSpace(name)
        ? name : $"未命名{kind switch { VaultItemKind.Login => "登录", VaultItemKind.Card => "支付卡", VaultItemKind.Identity => "身份", VaultItemKind.Note => "安全笔记", VaultItemKind.Ssh => "SSH 密钥", _ => "条目" }}";

    private void OnFieldUsed(object? sender, EventArgs e)
    {
        if (ViewModel.Detail is { } detail)
        {
            ViewModel.RecordRecentUse(detail.Id);
            UpdateDetailStatus();
        }
    }

    private void OnFieldRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FieldRow field)
            BindFieldAuthorization(field);
    }

    private void BindFieldAuthorization(FieldRow field)
    {
        field.IsReadOnly = ViewModel.Detail?.IsDeleted == true;
        var itemId = ViewModel.Detail?.Id;
        field.AuthorizeItemAsync = ct => itemId is null
            ? Task.FromResult(false)
            : ViewModel.AuthorizeFieldCopyAsync(itemId, PromptMasterPasswordAsync, ct);
    }

    private void OnRowNameLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock block)
            ApplySearchHighlight(block);
    }

    private void OnRowLoaded(object sender, RoutedEventArgs e) => UpdateRowActionVisibility(sender);

    private void RefreshRowVisuals()
    {
        if (CipherList is null)
            return;
        foreach (var row in Descendants<Grid>(CipherList).Where(row => row.Name == "RowRoot"))
        {
            var hovered = _rowActionStates.TryGetValue(row, out var state) && state.IsPointerOver;
            UpdateRowVisual(row, hovered);
        }
    }

    private void UpdateRowVisual(Grid row, bool hovered)
    {
        if (row.DataContext is not CipherListItem item)
            return;
        var selected = ViewModel.IsSelectionMode ? ViewModel.IsSelected(item.Id) : ViewModel.SelectedItem?.Id == item.Id;
        var dark = ActualTheme == ElementTheme.Dark;
        var alpha = selected ? dark ? (byte)18 : (byte)15 : hovered ? dark ? (byte)11 : (byte)9 : (byte)0;
        row.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(alpha,
            dark ? (byte)255 : (byte)0, dark ? (byte)255 : (byte)0, dark ? (byte)255 : (byte)0));
        if (row.FindName("RowSelectionIndicator") is FrameworkElement indicator)
            indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (row.FindName("RowNameText") is TextBlock name)
            name.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void ApplySearchHighlight(TextBlock block)
    {
        block.TextHighlighters.Clear();
        var query = ViewModel.SearchText.Trim();
        if (query.Length == 0)
            return;
        var index = block.Text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
            return;
        var highlighter = new Microsoft.UI.Xaml.Documents.TextHighlighter
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ActualTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(61, 96, 205, 255) : Windows.UI.Color.FromArgb(41, 0, 95, 184)),
        };
        highlighter.Ranges.Add(new Microsoft.UI.Xaml.Documents.TextRange { StartIndex = index, Length = query.Length });
        block.TextHighlighters.Add(highlighter);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private void AnimateDetail()
    {
        if (!IsLoaded || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, DetailViewPanel);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private async Task RefreshDetailSecurityAsync()
    {
        try
        {
            var report = await global::App.App.Services.GetRequiredService<IVaultHealthUiService>().AnalyzeOfflineAsync();
            if (!IsLoaded)
                return;
            _detailHealthReport = report;
            UpdateDetailStatus();
            foreach (var icon in Descendants<FontIcon>(CipherList).Where(icon => icon.Name == "RowIssueGlyph"))
                UpdateRowIssue(icon);
        }
        catch (OperationCanceledException exception) when (UiExceptionPolicy.IsGenuineCancellation(exception)) { }
        catch (OperationCanceledException exception) when (Api.RequestTimeoutClassifier.IsTimeout(exception))
        {
            ViewModel.OperationError = UiExceptionPolicy.RequestTimeoutMessage;
        }
        catch (VaultHealthUiException) { }
        catch (InvalidOperationException) { }
    }

    private void OnSecurityReportChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateDetailStatus();
        foreach (var icon in Descendants<FontIcon>(CipherList).Where(icon => icon.Name == "RowIssueGlyph"))
            UpdateRowIssue(icon);
    }

    private (string Text, string Glyph, string Brush, bool ChangePassword) GetIssue(CipherListItem item)
    {
        if (item.IsDeleted || item.IsArchived || item.Kind != VaultItemKind.Login)
            return (string.Empty, string.Empty, "TextFillColorSecondaryBrush", false);
        var reports = global::App.App.Services.GetRequiredService<SecurityReportViewModel>();
        var exposed = reports.ExposedItems.FirstOrDefault(finding => finding.Item.CipherId == item.Id);
        if (exposed is not null)
            return ($"密码出现在 {exposed.BreachCount} 次已知数据泄露中" + (reports.ExposedChecked ? string.Empty : " · 待复查"), "\uEA39", "SystemFillColorCriticalBrush", true);
        var reused = _detailHealthReport?.Reused.FirstOrDefault(group => group.Items.Any(entry => entry.CipherId == item.Id));
        if (reused is not null)
            return ($"与“{reused.Items.FirstOrDefault(entry => entry.CipherId != item.Id)?.Name}”用的是同一个密码", "\uE7BA", "SystemFillColorCautionBrush", true);
        if (_detailHealthReport?.Weak.Any(finding => finding.Item.CipherId == item.Id) == true)
            return ("密码较弱，建议更换", "\uE7BA", "SystemFillColorCautionBrush", true);
        return (string.Empty, string.Empty, "TextFillColorSecondaryBrush", false);
    }

    private void OnRowIssueLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon icon)
            UpdateRowIssue(icon);
    }

    private void UpdateRowIssue(FontIcon icon)
    {
        if (icon.DataContext is not CipherListItem item)
            return;
        var issue = GetIssue(item);
        icon.Visibility = string.IsNullOrEmpty(issue.Text) ? Visibility.Collapsed : Visibility.Visible;
        icon.Glyph = issue.Glyph;
        icon.Foreground = ThemeBrushResolver.Resolve(this, issue.Brush);
        ToolTipService.SetToolTip(icon, issue.Text);
    }

    private void UpdateDetailStatus()
    {
        if (DetailStatusText is null || ViewModel.SelectedItem is not { } item || ViewModel.Detail is not { } detail)
            return;
        var issue = GetIssue(item);
        var facts = new List<string>();
        if (!string.IsNullOrEmpty(issue.Text))
            facts.Add(issue.Text);
        else if (detail.IsDeleted)
            facts.Add("在回收站中");
        else if (detail is LoginDetail login && !string.IsNullOrEmpty(login.Password))
        {
            var score = global::App.App.Services.GetRequiredService<Crypto.PasswordStrength.PasswordStrengthEvaluator>().Evaluate(login.Password).Score;
            facts.Add(score >= 4 ? "密码很强" : score >= 3 ? "密码强度良好" : "密码强度一般");
        }
        else if (detail is CardDetail card)
            facts.Add(string.Join(" · ", new[] { card.Brand, card.Expiry }.Where(value => !string.IsNullOrWhiteSpace(value))));
        if (detail.IsArchived)
            facts.Add("已归档");
        if (!detail.IsDeleted && detail is LoginDetail loginDetails)
        {
            if (item.HasTotp) facts.Add("已开启两步验证");
            if (loginDetails.HasPasskeys) facts.Add("存有通行密钥");
            if (loginDetails.PasswordRevisionDate is { } changedAt)
            {
                var now = DateTimeOffset.UtcNow;
                var years = now.Year - changedAt.Year;
                if (changedAt.AddYears(years) > now)
                    years--;
                if (years >= 2)
                    facts.Add($"{years} 年没换过密码");
            }
        }
        if (!detail.IsDeleted)
            facts.Add(RecentUseText(ViewModel.GetLastUsedAt(item.Id)));
        DetailStatusText.Text = string.Join(" · ", facts.Where(value => !string.IsNullOrWhiteSpace(value)));
        DetailStatusIcon.Glyph = issue.Glyph;
        DetailStatusIcon.Visibility = string.IsNullOrEmpty(issue.Glyph) ? Visibility.Collapsed : Visibility.Visible;
        DetailStatusIcon.Foreground = ThemeBrushResolver.Resolve(this, issue.Brush);
        ChangePasswordButton.Visibility = issue.ChangePassword && item.CanModify ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string RecentUseText(DateTimeOffset? lastUsed)
    {
        if (lastUsed is null)
            return "还没用过";
        var elapsed = DateTimeOffset.UtcNow - lastUsed.Value;
        if (elapsed.TotalMinutes < 1) return "刚刚用过";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} 分钟前用过";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} 小时前用过";
        if (elapsed.TotalDays < 2) return "昨天用过";
        if (elapsed.TotalDays < 30) return $"{(int)elapsed.TotalDays} 天前用过";
        if (elapsed.TotalDays < 365) return $"{(int)(elapsed.TotalDays / 30)} 个月前用过";
        return $"{(int)(elapsed.TotalDays / 365)} 年前用过";
    }

    private async void OnChangePasswordClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ChangePasswordAsync(item.Id);
    }

    private void OnGenerateLoginPasswordClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEditEditorDraft || ViewModel.EditorDraft is not { } draft)
            return;
        draft.Login.Password = global::App.App.Services.GetRequiredService<GeneratorViewModel>().GenerateCredentialForLogin();
        LoginPasswordEditor.PasswordRevealMode = PasswordRevealMode.Visible;
    }

    private async void OnRowCopyUsernameClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            ShowRowCopyResult(sender, await ViewModel.CopyFieldAsync(item.Id, "username", PromptMasterPasswordAsync));
    }

    private async void OnRowCopyTotpClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            ShowRowCopyResult(sender, await ViewModel.CopyFieldAsync(item.Id, "totp", PromptMasterPasswordAsync));
    }

    private void ShowRowCopyResult(object sender, UiOperationResult result)
    {
        if (!result.IsSuccess)
        {
            if (result.Status == UiOperationStatus.Failed)
                ViewModel.OperationError = result.Error ?? "复制失败，请重试。";
            return;
        }
        ResetRowCopyFeedback();
        if (sender is Button { Content: FontIcon icon } button)
        {
            _copiedRowButton = button;
            _copiedRowGlyph = icon.Glyph;
            _copiedRowVersion = (global::App.App.Services.GetService<IClipboardService>() as IClipboardStatus)?.WriteVersion ?? 0;
            icon.Glyph = "\uE73E";
            icon.Foreground = ThemeBrushResolver.Resolve(this, "SystemFillColorSuccessBrush");
        }
    }

    private void OnClipboardChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        var status = global::App.App.Services.GetService<IClipboardService>() as IClipboardStatus;
        if (status?.HasContent != true || status.WriteVersion != _copiedRowVersion)
            ResetRowCopyFeedback();
    });

    private void ResetRowCopyFeedback()
    {
        if (_copiedRowButton?.Content is FontIcon icon && _copiedRowGlyph is not null)
        {
            icon.Glyph = _copiedRowGlyph;
            icon.ClearValue(FontIcon.ForegroundProperty);
        }
        _copiedRowButton = null;
        _copiedRowGlyph = null;
        _copiedRowVersion = 0;
    }

    private void OnRowRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (sender is Grid row && row.FindName("RowMoreButton") is Button { Flyout: { } flyout })
        {
            flyout.ShowAt(row);
            e.Handled = true;
        }
    }

    private async void OnNewFromSearchClick(object sender, RoutedEventArgs e)
    {
        var name = ViewModel.SearchText.Trim();
        if (!await ConfirmLeaveEditorAsync())
            return;
        ViewModel.SearchText = string.Empty;
        BeginNewItemCore(VaultItemKind.Login);
        if (ViewModel.EditorDraft is { } draft)
            draft.Name = name;
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e) => ViewModel.SearchText = string.Empty;

    private async void OnVaultKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Handled || _sensitiveActionPending || _confirmingLeave)
            return;
        var key = e.Key;
        if (key == Windows.System.VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(ViewModel.SearchText))
                ViewModel.SearchText = string.Empty;
            else if (ViewModel.IsEditing)
                await ConfirmLeaveEditorAsync();
            else
                return;
            e.Handled = true;
            return;
        }
        if (XamlRoot is null || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
            return;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or AutoSuggestBox or RichEditBox || ViewModel.IsEditing)
            return;
        var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (control && key == Windows.System.VirtualKey.F)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (control && key == Windows.System.VirtualKey.C && !HasSelectedText(this))
        {
            if (ViewModel.SelectedItem is { IsDeleted: false } item)
            {
                if (ViewModel.Detail?.Id == item.Id && item.Kind is VaultItemKind.Login or VaultItemKind.Card or VaultItemKind.Identity or VaultItemKind.Ssh)
                    await DetailCopyStrip.CopyPrimaryAsync();
                else
                    await ViewModel.CopyPrimaryAsync(item.Id, PromptMasterPasswordAsync);
            }
            e.Handled = true;
        }
        else if (key == Windows.System.VirtualKey.F2 && ViewModel.SelectedItem is { IsDeleted: false })
        {
            OnEditCipherClick(sender, e);
            e.Handled = true;
        }
        else if (key == Windows.System.VirtualKey.Delete && ViewModel.SelectedItem is { IsDeleted: false })
        {
            OnDeleteCipherClick(sender, e);
            e.Handled = true;
        }
        else if (key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
            e.Handled = MoveSearchSelection(key == Windows.System.VirtualKey.Up ? -1 : 1);
    }

    private static bool HasSelectedText(DependencyObject parent)
    {
        if (parent is TextBlock { SelectedText.Length: > 0 } or RichTextBlock { SelectedText.Length: > 0 })
            return true;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
            if (HasSelectedText(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index)))
                return true;
        return false;
    }

    private void ShowToast(string message, Func<Task<UiOperationResult>>? undo = null)
    {
        VaultToastText.Text = message;
        VaultToast.Visibility = Visibility.Visible;
        _undoAction = undo;
        VaultUndoButton.Visibility = undo is null ? Visibility.Collapsed : Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(undo is null ? 5 : 8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            VaultToast.Visibility = Visibility.Collapsed;
            _undoAction = null;
        };
        _toastTimer.Start();
    }

    private async Task DeleteWithUndoAsync(CipherListItem item)
    {
        if (item.IsDeleted || !await ConfirmLeaveEditorAsync())
            return;
        if ((await ViewModel.SoftDeleteAsync(item.Id)).IsSuccess)
            ShowToast($"已将“{item.Name}”移到回收站", () => ViewModel.RestoreAsync(item.Id));
    }

    private async Task ArchiveWithUndoAsync(CipherListItem item)
    {
        if (!await ConfirmLeaveEditorAsync())
            return;
        if ((await ViewModel.ArchiveAsync(item.Id)).IsSuccess)
            ShowToast($"已归档“{item.Name}”", () => ViewModel.UnarchiveAsync(item.Id));
    }

    private async Task UnarchiveWithUndoAsync(CipherListItem item)
    {
        if (!await ConfirmLeaveEditorAsync())
            return;
        if ((await ViewModel.UnarchiveAsync(item.Id)).IsSuccess)
            ShowToast($"已取消归档“{item.Name}”", () => ViewModel.ArchiveAsync(item.Id));
    }

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_undoAction is not { } undo)
            return;
        _undoAction = null;
        var result = await undo();
        if (result.IsSuccess)
            ShowToast("已撤销");
        else
        {
            VaultToast.Visibility = Visibility.Collapsed;
            ViewModel.OperationError = result.Error ?? "无法撤销，请同步后重试。";
        }
    }

    private sealed record OrganizationTransferDialogResult(
        CipherDestinationOption Destination,
        IReadOnlyList<string> CollectionIds);
}
