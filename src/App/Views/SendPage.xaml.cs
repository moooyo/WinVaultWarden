using App.Controls;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;

namespace App.Views;

public sealed partial class SendPage : Page
{
    public SendViewModel ViewModel { get; }
    public SendEditorDraft Draft { get; private set; } = SendEditorDraft.CreateDefault(SendType.Text);
    public bool IsReceiveView => _isReceive;
    public string CurrentFilterTag => _isReceive ? "send:receive" : ViewModel.SelectedFilterTag;
    public Visibility EmptyStateVisibility(bool isEmpty) => isEmpty && _draft is null ? Visibility.Visible : Visibility.Collapsed;
    public string HideEmailState(bool isOn) => isOn ? "开 · 对方看不到是谁发送的" : "关";
    public string DisabledState(bool isOn) => isOn ? "开 · 任何人都打不开链接" : "关";
    private ListView SendsList => SendContentList;
    private readonly AsyncOperationGate _receivedFileSaveGate = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _availabilityTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _copyTimer;
    private readonly IClipboardStatus? _clipboardStatus;
    private long _copiedLinkVersion;
    private CancellationTokenSource? _pageLifetimeCts;
    private CancellationTokenSource? _editorSaveCts;
    private IDisposable? _sensitiveRegistration;
    private int _receivedFileSaveInProgress;
    private SendListItem? _selectedItem;
    private SendListItem? _editingItem;
    private SendEditorDraft? _draft;
    private SendEditorDraft? _detailDraft;
    private long _selectionRequest;
    private bool _isReceive;
    private bool _changingSelection;
    private bool _initializingEditor;
    private bool _editorDirty;
    private EditorSnapshot? _editorBaseline;
    private bool _isSavingEditor;
    private readonly List<(Control Control, bool WasEnabled)> _editorEnabledStates = new();
    private bool _hasNavigated;

    private sealed record PageRequest(
        long Generation,
        CancellationTokenSource Lifetime,
        CancellationToken Token,
        Core.Session.VaultSessionLease Session);

    private bool TryBeginPageRequest(out PageRequest request)
    {
        var generation = Interlocked.Increment(ref _selectionRequest);
        if (!TryCapturePageLifetime(out var lifetime, out var token) || !TryCaptureExpectedLease(out var session))
        {
            request = null!;
            return false;
        }
        request = new PageRequest(generation, lifetime, token, session);
        return true;
    }

    private bool TryCapturePageLifetime(out CancellationTokenSource lifetime, out CancellationToken token)
    {
        lifetime = _pageLifetimeCts!;
        token = default;
        if (lifetime is null) return false;
        try
        {
            token = lifetime.Token;
            return !token.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private bool IsCurrentPageRequest(PageRequest request) =>
        request.Generation == Volatile.Read(ref _selectionRequest)
        && ReferenceEquals(request.Lifetime, _pageLifetimeCts)
        && !request.Token.IsCancellationRequested;

    private bool TryCommitPageRequest(PageRequest request, Action commit)
    {
        var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var applied = false;
        return lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(request.Session, () =>
        {
            if (!IsCurrentPageRequest(request)) return;
            commit();
            applied = true;
        })) && applied;
    }

    public SendPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SendViewModel>();
        NavigationCacheMode = NavigationCacheMode.Enabled;
        _clipboardStatus = global::App.App.Services.GetRequiredService<IClipboardService>() as IClipboardStatus;
        InitializeComponent();
        KeyDown += OnPageKeyDown;
        _copyTimer = DispatcherQueue.CreateTimer();
        _copyTimer.Interval = TimeSpan.FromSeconds(1);
        _copyTimer.Tick += (_, _) => UpdateLinkCopyFeedback();
        _availabilityTimer = DispatcherQueue.CreateTimer();
        _availabilityTimer.Interval = TimeSpan.FromSeconds(30);
        _availabilityTimer.Tick += (_, _) =>
        {
            ViewModel.RefreshAvailability(DateTimeOffset.UtcNow);
            if (_selectedItem is not null)
            {
                DetailStatus.Text = StatusText(_selectedItem with { DisplayTime = DateTimeOffset.UtcNow });
                UpdateStatusAction(_selectedItem);
            }
        };
        MaxAccessTextBox.TextChanged += (_, _) => { if (!_initializingEditor && _draft is not null) _editorDirty = true; };
        EditorContent.SizeChanged += (_, _) => UpdateEditorValueLayout();
        SendEditorErrorInfoBar.Closed += (_, _) => SendEditorErrorInfoBar.Visibility = Visibility.Collapsed;
        ActualThemeChanged += (_, _) => { if (_selectedItem is { } item) UpdateStatusAction(item); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_isSavingEditor) ReplacePageLifetime();
        _sensitiveRegistration?.Dispose();
        _sensitiveRegistration = global::App.App.Services.GetRequiredService<ITransientOperationRegistry>()
            .Register(CancelPageLifetime, ClearPageSensitive, ClearPageSensitive);
        _availabilityTimer.Start();
        SetReceivedFileSaveBusy(Volatile.Read(ref _receivedFileSaveInProgress) != 0);
        if (!_hasNavigated && e.Parameter is string tag)
            SelectFilter(tag);
        _hasNavigated = true;
        if (_draft is null) _ = LoadInitialAsync();
        UpdateAdaptiveLayout();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (!_isSavingEditor) CancelPageLifetime();
        _availabilityTimer.Stop();
        _copyTimer.Stop();
        ViewModel.CancelLoad();
        if (!_isSavingEditor) ViewModel.CancelReceive();
        Interlocked.Increment(ref _selectionRequest);
    }

    public async Task<bool> CanNavigateAwayAsync()
    {
        if (_isSavingEditor) return false;
        if (_draft is null || !_editorDirty || CaptureEditorSnapshot() == _editorBaseline) return true;
        var draft = _draft;
        var generation = Volatile.Read(ref _selectionRequest);
        var snapshot = CaptureEditorSnapshot();
        var confirmed = await DialogHelper.ConfirmAsync(XamlRoot, "放弃未保存的更改？",
            $"“{(string.IsNullOrWhiteSpace(draft.Name) ? "新 Send" : draft.Name)}”的更改还没有保存。", "放弃更改");
        if (!ReferenceEquals(draft, _draft) || generation != Volatile.Read(ref _selectionRequest)
            || snapshot != CaptureEditorSnapshot()) return false;
        if (confirmed) CloseEditor();
        return confirmed;
    }

    public async void SelectFilter(string tag)
    {
        if (!TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync()) return;
        TryCommitPageRequest(request, () =>
        {
        CloseEditor();
        _isReceive = tag is "send:receive" or "send:received";
        BrowsePane.Visibility = _isReceive ? Visibility.Collapsed : Visibility.Visible;
        ReceivePane.Visibility = _isReceive ? Visibility.Visible : Visibility.Collapsed;
        if (_isReceive)
            ReceivedLinkTextBox.Focus(FocusState.Programmatic);
        else
        {
            ResetSelectedDetail();
            ViewModel.SelectFilterByTag(tag);
            ReconcileFilteredSelection();
        }
        UpdateAdaptiveLayout();
        });
    }

    public void ApplySearch(string query)
    {
        ViewModel.SearchQuery = query ?? string.Empty;
        ReconcileFilteredSelection();
    }

    private void ReconcileFilteredSelection()
    {
        if (_draft is not null || _isReceive) return;
        var matching = ViewModel.FilteredItems.FirstOrDefault(item => item.Id == _selectedItem?.Id);
        if (matching is not null) { SendsList.SelectedItem = matching; return; }
        ResetSelectedDetail();
        if ((XamlRoot?.Size.Width ?? ActualWidth) >= 800)
            SendsList.SelectedItem = ViewModel.FilteredItems.FirstOrDefault();
        UpdateAdaptiveLayout();
    }

    private void ResetSelectedDetail()
    {
        Interlocked.Increment(ref _selectionRequest);
        _selectedItem = null;
        _detailDraft?.ClearSensitive();
        _detailDraft = null;
        ViewModel.SelectedMenuItem = null;
        SelectedMoreButton.DataContext = null;
        SendsList.SelectedItem = null;
        DetailScroll.Visibility = Visibility.Collapsed;
        ChooseSendHint.Visibility = Visibility.Visible;
        SendTextContent.Text = string.Empty;
        DetailNotes.Text = string.Empty;
        SetShareLinkDisplay(null);
        _copiedLinkVersion = 0;
        _copyTimer.Stop();
    }

    public void MoveSearchSelection(int offset)
    {
        if (_isReceive || _draft is not null || ViewModel.FilteredItems.Count == 0) return;
        var index = Math.Clamp(SendsList.SelectedIndex + offset, 0, ViewModel.FilteredItems.Count - 1);
        SendsList.SelectedIndex = index;
        SendsList.ScrollIntoView(ViewModel.FilteredItems[index]);
    }

    public void AcceptSearchResult()
    {
        if (_isReceive || _draft is not null || ViewModel.FilteredItems.Count == 0) return;
        SendsList.SelectedIndex = 0;
        SendsList.Focus(FocusState.Programmatic);
    }

    public async void BeginCreate(SendType type = SendType.Text)
    {
        if (!ViewModel.CanCreateSend || !TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync()) return;
        var draft = SendEditorDraft.CreateDefault(type);
        draft.OriginatingSession = request.Session;
        if (!TryCommitPageRequest(request, () =>
        {
            CloseEditor();
            if (_isReceive) ViewModel.SelectFilterByTag("send:all");
            OpenEditor(null, draft);
        })) draft.ClearSensitive();
    }

    public void RefreshSendList() => _ = LoadInitialAsync();

    private async Task LoadInitialAsync()
    {
        if (!TryCapturePageLifetime(out var lifetime, out var token) || !TryCaptureExpectedLease(out var session)) return;
        var request = new PageRequest(Volatile.Read(ref _selectionRequest), lifetime, token, session);
        var selectedId = _selectedItem?.Id;
        await ViewModel.LoadAsync(request.Token);
        if (_draft is not null) return;
        TryCommitPageRequest(request, () =>
        {
        var item = ViewModel.FilteredItems.FirstOrDefault(item => item.Id == selectedId);
        if (item is null && (XamlRoot?.Size.Width ?? ActualWidth) >= 800)
            item = ViewModel.FilteredItems.FirstOrDefault();
        if (item is not null) SendsList.SelectedItem = item;
        });
    }

    private static SendListItem? ItemFromSender(object sender) =>
        sender is FrameworkElement { DataContext: SendListItem item } ? item : null;

    private SendListItem? ResolveItem(object sender) =>
        ItemFromSender(sender) ?? ViewModel.SelectedMenuItem ?? _selectedItem;

    private void OnAddSendClick(object sender, RoutedEventArgs e) => BeginCreate();
    private void OnRetryClick(object sender, RoutedEventArgs e) => RefreshSendList();

    private async void OnSendSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || SendsList.SelectedItem is not SendListItem item) return;
        if (_selectedItem is { } selected && _draft is null
            && (item with { DisplayTime = selected.DisplayTime }) == selected) return;
        if (!TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync())
        {
            if (!IsCurrentPageRequest(request)) return;
            _changingSelection = true;
            SendsList.SelectedItem = _selectedItem;
            _changingSelection = false;
            return;
        }
        Task detailTask = Task.CompletedTask;
        if (TryCommitPageRequest(request, () =>
        {
            CloseEditor();
            detailTask = ShowDetailAsync(item);
        })) await detailTask;
    }

    private async Task ShowDetailAsync(SendListItem item)
    {
        if (!TryBeginPageRequest(out var request)) return;
        _selectedItem = item;
        ViewModel.SelectedMenuItem = null;
        _detailDraft?.ClearSensitive();
        _detailDraft = null;
        DetailName.Text = item.Name;
        DetailGlyph.Glyph = item.Glyph;
        DetailSubtitle.Text = $"{item.TypeLabel} Send";
        DetailStatus.Text = StatusText(item);
        UpdateStatusAction(item);
        EditSelectedSendButton.IsEnabled = ViewModel.CanModifySend(item);
        SelectedMoreButton.DataContext = item;
        SetShareLinkDisplay(item.Link);
        ShareLinkLabel.Text = "分享链接";
        ShareLinkProgress.Visibility = Visibility.Collapsed;
        _copiedLinkVersion = 0;
        DetailDeletion.Text = FormatDate(item.DeletionDateTime);
        DetailDeletionHint.Text = $"{item.DeletionRelativeLabel}连同链接一起删除";
        DetailExpiration.Text = item.ExpirationDate is { } expiration ? FormatDate(expiration) : "永不过期";
        DetailAccessLimit.Text = item.MaxAccessCount is { } max ? $"{max} 次" : "不限";
        DetailAccessCount.Text = $"已访问 {item.AccessCount} 次";
        DetailPassword.Text = item.HasPassword ? "已设置 · 对方打开链接时需要输入" : "未设置";
        DetailHideEmail.Text = "—";
        SendTextContent.Text = item.TextContent ?? string.Empty;
        DetailFileName.Text = item.FileName ?? item.Name;
        DetailFileSize.Text = FormatSize(item.FileSize);
        DetailModified.Text = item.RevisionDate is { } revision ? $"修改于 {FormatDate(revision)}" : string.Empty;
        NotesDetailRow.Visibility = Visibility.Collapsed;
        TextDetailBlock.Visibility = item.Type == SendType.Text ? Visibility.Visible : Visibility.Collapsed;
        FileDetailBlock.Visibility = item.Type == SendType.File ? Visibility.Visible : Visibility.Collapsed;
        ChooseSendHint.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;
        EditorScroll.Visibility = Visibility.Collapsed;
        UpdateAdaptiveLayout();
        try
        {
            var detail = await LoadDraftAsync(item.Id, request);
            if (detail is null) return;
            if (_draft is not null || !request.Session.SameSessionAs(detail.OriginatingSession)
                || !TryCommitPageRequest(request, () =>
                {
                    _detailDraft = detail;
                    SendTextContent.Text = detail.Text;
                    DetailFileName.Text = detail.FileName;
                    DetailHideEmail.Text = detail.HideEmail ? "是 · 对方看不到是谁发送的" : "否";
                    DetailNotes.Text = detail.Notes;
                    NotesDetailRow.Visibility = string.IsNullOrWhiteSpace(detail.Notes) ? Visibility.Collapsed : Visibility.Visible;
                }))
            {
                detail.ClearSensitive();
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryCommitPageRequest(request, () => ViewModel.Error = "无法加载 Send 详情，请刷新后重试。");
        }
    }

    private static string FormatDate(DateTimeOffset? date) =>
        date is { } value ? value.ToLocalTime().ToString("M 月 d 日 HH:mm") : "待服务器确认";

    private static string FormatSize(long? bytes) => bytes switch
    {
        null => string.Empty,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes / (1024d * 1024):0.#} MB",
    };

    private static string StatusText(SendListItem item)
    {
        var state = item.GetAvailability(DateTimeOffset.UtcNow).State switch
        {
            SendAvailabilityState.Available => "可以访问",
            SendAvailabilityState.Disabled => "已禁用，任何人都打不开这个链接",
            SendAvailabilityState.Expired => $"已于 {FormatDate(item.ExpirationDate)} 过期",
            SendAvailabilityState.Deleted => "已到删除时间",
            SendAvailabilityState.AccessLimitReached => "已达到最大访问次数",
            _ => "等待服务器确认",
        };
        return $"{state} · {item.DeletionRelativeLabel}自动删除 · 已访问 {item.AccessCount} 次{(item.HasPassword ? " · 需要访问密码" : string.Empty)}";
    }

    private void UpdateStatusAction(SendListItem item)
    {
        var state = item.GetAvailability(DateTimeOffset.UtcNow).State;
        var label = state switch
        {
            SendAvailabilityState.Disabled => "启用",
            SendAvailabilityState.Expired => "延长有效期",
            SendAvailabilityState.AccessLimitReached => "提高上限",
            _ => null,
        };
        DetailStatusAction.Content = label;
        DetailStatusAction.IsEnabled = ViewModel.CanModifySend(item);
        DetailStatusAction.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        var good = state == SendAvailabilityState.Available;
        DetailStatusBadge.Background = good
            ? ThemeBrushResolver.Resolve(this, "SystemFillColorSuccessBrush")
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        DetailStatusIcon.Glyph = good ? "\uE73E" : state == SendAvailabilityState.Disabled ? "\uE733" : "\uE7BA";
        DetailStatusIcon.FontSize = good ? 8 : 14;
        DetailStatusIcon.Foreground = ThemeBrushResolver.Resolve(this,
            good ? "WardenLayerBrush" : state == SendAvailabilityState.Disabled
                ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
    }

    private void SetShareLinkDisplay(string? link)
    {
        if (string.IsNullOrEmpty(link))
        {
            ShareLinkHostRun.Text = string.Empty;
            ShareLinkPathRun.Text = string.Empty;
            return;
        }
        var scheme = link.IndexOf("://", StringComparison.Ordinal);
        var display = scheme >= 0 ? link[(scheme + 3)..] : link;
        var path = display.IndexOf('/');
        ShareLinkHostRun.Text = path >= 0 ? display[..path] : display;
        ShareLinkPathRun.Text = path >= 0 ? display[path..] : string.Empty;
    }
    private async void OnDetailStatusActionClick(object sender, RoutedEventArgs e)
    {
        if (_selectedItem?.Disabled == true) OnToggleDisabledClick(this, e);
        else await EditItemAsync(_selectedItem);
    }

    private async void OnEditSendClick(object sender, RoutedEventArgs e) => await EditItemAsync(ResolveItem(sender));

    private async Task EditItemAsync(SendListItem? item)
    {
        if (item is null || !ViewModel.CanModifySend(item) || !TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync() || !IsCurrentPageRequest(request)) return;
        var draft = await LoadDraftAsync(item.Id, request);
        if (draft is null) return;
        if (!request.Session.SameSessionAs(draft.OriginatingSession)
            || !TryCommitPageRequest(request, () =>
            {
                CloseEditor();
                OpenEditor(item, draft);
            }))
        {
            draft.ClearSensitive();
        }
    }

    private async Task<SendEditorDraft?> LoadDraftAsync(string id, PageRequest request)
    {
        var token = request.Token;
        try { return await ViewModel.GetEditDraftAsync(id, token); }
        catch (Exception ex) when (Api.RequestTimeoutClassifier.IsTimeout(ex, token)
            || ex is HttpRequestException or IOException or UnauthorizedAccessException or Core.Services.OrganizationPolicyException)
        {
            TryCommitPageRequest(request, () =>
            {
                ViewModel.Error = Api.RequestTimeoutClassifier.IsTimeout(ex, token)
                    ? UiExceptionPolicy.RequestTimeoutMessage : "无法加载 Send 详情，请刷新后重试。";
            });
            return null;
        }
    }

    private void OpenEditor(SendListItem? item, SendEditorDraft draft)
    {
        Interlocked.Increment(ref _selectionRequest);
        _detailDraft?.ClearSensitive();
        _detailDraft = null;
        _draft = draft;
        _editingItem = item;
        if (item is null)
        {
            _changingSelection = true;
            SendsList.SelectedItem = null;
            _changingSelection = false;
        }
        _initializingEditor = true;
        Draft = draft;
        Bindings.Update();
        EditorGlyph.Glyph = draft.IsFile ? "\uE8A5" : "\uE8E4";
        SaveEditorButton.Content = item is null ? "创建并复制链接" : "保存";
        SendPasswordBox.PlaceholderText = draft.ExistingHasPassword ? "已设置 · 留空则保持不变" : "可选";
        RemovePasswordButton.Visibility = draft.ExistingHasPassword ? Visibility.Visible : Visibility.Collapsed;
        RemovePasswordButton.Content = "移除密码";
        MaxAccessTextBox.Text = draft.MaxAccessCount?.ToString() ?? string.Empty;
        EditorAccessHint.Text = item is null ? "留空表示不限次数" : $"已访问 {item.AccessCount} 次 · 留空表示不限";
        EditorTypeRow.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateEditorFileSummary();
        FillDateChoices(draft, item is not null);
        UpdateEditorType();
        UpdateDraftRow();
        _initializingEditor = false;
        _editorDirty = false;
        _editorBaseline = CaptureEditorSnapshot();
        draft.PropertyChanged += OnDraftChanged;
        SendEditorErrorInfoBar.IsOpen = false;
        SendEditorErrorInfoBar.Visibility = Visibility.Collapsed;
        _isReceive = false;
        BrowsePane.Visibility = Visibility.Visible;
        ReceivePane.Visibility = Visibility.Collapsed;
        ChooseSendHint.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Collapsed;
        EditorScroll.Visibility = Visibility.Visible;
        UpdateAdaptiveLayout();
        SendNameTextBox.Focus(FocusState.Programmatic);
    }

    private void FillDateChoices(SendEditorDraft draft, bool editing)
    {
        DeletionChoice.Items.Clear();
        if (editing) DeletionChoice.Items.Add($"保持不变 · {FormatDate(draft.DeletionDate)}");
        foreach (var label in new[] { "1 小时后", "1 天后", "2 天后", "3 天后", "7 天后", "30 天后" }) DeletionChoice.Items.Add(label);
        DeletionChoice.SelectedIndex = editing ? 0 : 4;
        ExpirationChoice.Items.Clear();
        if (editing && draft.ExpirationDate is not null) ExpirationChoice.Items.Add($"保持不变 · {FormatDate(draft.ExpirationDate)}");
        foreach (var label in new[] { "永不过期", "1 小时后", "1 天后", "2 天后", "3 天后", "7 天后" }) ExpirationChoice.Items.Add(label);
        ExpirationChoice.SelectedIndex = 0;
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initializingEditor) _editorDirty = true;
        if (e.PropertyName is nameof(SendEditorDraft.Name) or nameof(SendEditorDraft.Type)) UpdateDraftRow();
        if (e.PropertyName == nameof(SendEditorDraft.Password) && _draft is { } draft)
            RemovePasswordButton.Visibility = draft.ExistingHasPassword && string.IsNullOrEmpty(draft.Password) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnRemovePasswordClick(object sender, RoutedEventArgs e)
    {
        if (_draft is null) return;
        _draft.RemovePassword = !_draft.RemovePassword;
        RemovePasswordButton.Content = _draft.RemovePassword ? "保存后移除 · 撤销" : "移除密码";
    }
    private void OnTextTypeClick(object sender, RoutedEventArgs e) => SetEditorType(SendType.Text);
    private void OnFileTypeClick(object sender, RoutedEventArgs e) => SetEditorType(SendType.File);
    private void SetEditorType(SendType type)
    {
        if (_draft is null || _editingItem is not null) return;
        SendEditorDialogLifecycle.SetType(_draft, type, true, null);
        UpdateEditorType();
        UpdateEditorFileSummary();
    }
    private void UpdateEditorType()
    {
        if (_draft is null) return;
        TextSendTypeButton.IsChecked = _draft.IsText;
        FileSendTypeButton.IsChecked = _draft.IsFile;
        EditorTextRow.Visibility = _draft.IsText ? Visibility.Visible : Visibility.Collapsed;
        EditorFileRow.Visibility = _draft.IsFile ? Visibility.Visible : Visibility.Collapsed;
        EditorGlyph.Glyph = _draft.IsFile ? "\uE8A5" : "\uE8E4";
    }
    private void UpdateDraftRow()
    {
        DraftRow.Visibility = _draft is not null && _editingItem is null ? Visibility.Visible : Visibility.Collapsed;
        if (_draft is null) return;
        DraftRowName.Text = string.IsNullOrWhiteSpace(_draft.Name)
            ? _draft.IsFile ? "新建文件 Send" : "新建文本 Send" : _draft.Name;
        DraftRowGlyph.Glyph = _draft.IsFile ? "\uE8A5" : "\uE8E4";
    }
    private void OnDeletionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingEditor || _draft is null || DeletionChoice.SelectedItem is not string label) return;
        if (label.StartsWith("保持不变", StringComparison.Ordinal))
        {
            if (_editorBaseline is { } baseline)
            {
                _draft.DeletionDateLabel = baseline.DeletionDateLabel;
                _draft.DeletionDate = baseline.DeletionDate;
            }
            return;
        }
        _draft.DeletionDateLabel = "自定义";
        _draft.DeletionDate = RelativeDate(label);
    }
    private void OnExpirationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingEditor || _draft is null || ExpirationChoice.SelectedItem is not string label) return;
        if (label.StartsWith("保持不变", StringComparison.Ordinal))
        {
            if (_editorBaseline is { } baseline) _draft.ExpirationDate = baseline.ExpirationDate;
            return;
        }
        _draft.ExpirationDate = label == "永不过期" ? null : RelativeDate(label);
    }

    private EditorSnapshot? CaptureEditorSnapshot() => _draft is not { } draft ? null : new(
        draft.Type, draft.Name, draft.Text, draft.FileName, draft.Notes, draft.Password,
        draft.HideTextByDefault, draft.HideEmail, draft.Disabled, draft.RemovePassword,
        int.TryParse(MaxAccessTextBox.Text, out var count) ? count : null,
        draft.DeletionDateLabel, draft.DeletionDate, draft.ExpirationDate, draft.ReplacementFile);

    private sealed record EditorSnapshot(
        SendType Type, string Name, string Text, string FileName, string Notes, string Password,
        bool HideText, bool HideEmail, bool Disabled, bool RemovePassword, int? MaxAccessCount,
        string DeletionDateLabel, DateTimeOffset? DeletionDate, DateTimeOffset? ExpirationDate,
        Core.Models.SendFileSource? ReplacementFile);
    private static DateTimeOffset RelativeDate(string label) => label == "1 小时后"
        ? DateTimeOffset.UtcNow.AddHours(1)
        : DateTimeOffset.UtcNow.AddDays(int.Parse(label.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture));
    private void OnMaxAccessBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args) =>
        args.Cancel = args.NewText.Any(character => !char.IsAsciiDigit(character));

    private async void OnChooseFileClick(object sender, RoutedEventArgs e)
    {
        var draft = _draft;
        if (draft is null || _isSavingEditor) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker { ViewMode = Windows.Storage.Pickers.PickerViewMode.List };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow));
        try
        {
            var file = await FilePickerHelper.PickSingleAsync(picker, _pageLifetimeCts?.Token ?? default);
            if (file is null || !ReferenceEquals(draft, _draft)) return;
            var source = await StorageFileSendSource.CreateAsync(file);
            if (!ReferenceEquals(draft, _draft)) return;
            draft.ReplacementFile = source;
            draft.FileName = file.Name;
            if (string.IsNullOrWhiteSpace(draft.Name)) draft.Name = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            ChooseFileButton.Content = "更换文件…";
            UpdateEditorFileSummary();
            _editorDirty = true;
            if (_editingItem is not null)
            {
                SendEditorErrorInfoBar.Severity = InfoBarSeverity.Warning;
                SendEditorErrorInfoBar.Message = "更换文件会创建新的 Send，原链接失效。保存后请向接收者发送新链接。";
                SendEditorErrorInfoBar.Visibility = Visibility.Visible;
                SendEditorErrorInfoBar.IsOpen = true;
                if (draft.ExistingHasPassword && !draft.RemovePassword)
                    SendPasswordBox.PlaceholderText = "更换文件需要重新输入访问密码";
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, _pageLifetimeCts?.Token ?? default)) { }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        { ShowEditorError(ex is ArgumentOutOfRangeException ? "文件超过 500 MiB 上限。" : "无法读取所选文件，请重新选择。"); }
    }

    private async void OnSaveEditorClick(object sender, RoutedEventArgs e)
    {
        var draft = _draft;
        var item = _editingItem;
        if (draft is null || _isSavingEditor) return;
        if (string.IsNullOrWhiteSpace(draft.Name)) { ShowEditorError("请填写名称"); SendNameTextBox.Focus(FocusState.Programmatic); return; }
        if (draft.IsText && string.IsNullOrWhiteSpace(draft.Text)) { ShowEditorError("请填写要分享的内容"); SendTextBox.Focus(FocusState.Programmatic); return; }
        if (draft.IsFile && (string.IsNullOrWhiteSpace(draft.FileName) || (item is null && draft.ReplacementFile is null))) { ShowEditorError("请选择要分享的文件"); return; }
        var replacingFile = item is not null && draft.IsFile && draft.ReplacementFile is not null;
        if (replacingFile && draft.ExistingHasPassword && !draft.RemovePassword && string.IsNullOrWhiteSpace(draft.Password))
        {
            ShowEditorError("原 Send 已设置访问密码。更换文件需要重新输入原密码或设置新密码，也可以明确选择移除密码。");
            SendPasswordBox.Focus(FocusState.Programmatic);
            return;
        }
        draft.MaxAccessCount = int.TryParse(MaxAccessTextBox.Text, out var limit) ? limit : null;
        _isSavingEditor = true;
        SetEditorBusy(true);
        SaveEditorButton.Content = item is null ? "正在创建…" : "正在保存…";
        using var saveCts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts?.Token ?? default);
        _editorSaveCts = saveCts;
        try
        {
            if (replacingFile)
            {
                var passwordMessage = draft.ExistingHasPassword && draft.RemovePassword
                    ? " 新的 Send 将不再设置访问密码。" : string.Empty;
                if (draft.OriginatingSession is not { } expectedSession
                    || !await DialogHelper.ConfirmAsync(XamlRoot, expectedSession, "更换文件并创建新链接？",
                        "更换文件会创建新的 Send，原链接失效。原 Send 会被删除，接收者需要使用新的分享链接。" + passwordMessage,
                        "更换文件")
                    || !ReferenceEquals(draft, _draft)
                    || saveCts.IsCancellationRequested)
                    return;
            }
            var result = item is null
                ? await ViewModel.CreateSendAsync(draft, draft.ReplacementFile, saveCts.Token)
                : await ViewModel.UpdateSendFromDraftAsync(item, draft, draft.ReplacementFile, saveCts.Token);
            if (!ReferenceEquals(draft, _draft)) return;
            if (!result.IsSuccess) { if (result.Status == UiOperationStatus.Failed) ShowEditorError(result.Error ?? "保存 Send 失败，请重试。"); return; }
            var committed = ViewModel.LastCommittedItem;
            var name = draft.Name;
            CloseEditor();
            if (committed is not null)
            {
                if (item is null)
                {
                    ViewModel.SelectFilterByTag("send:all");
                    ViewModel.SearchQuery = string.Empty;
                    ViewModel.CopyLinkCommand.Execute(committed);
                    if (ViewModel.HasCopyFeedback) ViewModel.CopyFeedback = $"已创建“{name}” · 链接已复制，可以直接粘贴给对方";
                }
                else if (replacingFile && committed.ReconciliationState == SendReconciliationState.None)
                {
                    ViewModel.CopyLinkCommand.Execute(committed);
                    if (ViewModel.HasCopyFeedback)
                        ViewModel.CopyFeedback = "已创建并复制新链接，原链接已失效。请将新链接发送给接收者。";
                }
                else if (committed.ReconciliationState == SendReconciliationState.None)
                    ViewModel.CopyFeedback = $"已保存“{name}”";
                _changingSelection = true;
                SendsList.SelectedItem = ViewModel.FilteredItems.FirstOrDefault(value => value.Id == committed.Id);
                _changingSelection = false;
                await ShowDetailAsync(committed);
            }
        }
        finally
        {
            _editorSaveCts = null;
            _isSavingEditor = false;
            SetEditorBusy(false);
            SaveEditorButton.Content = _editingItem is null ? "创建并复制链接" : "保存";
        }
    }

    private void SetEditorBusy(bool busy)
    {
        if (!busy)
        {
            foreach (var (control, wasEnabled) in _editorEnabledStates) control.IsEnabled = wasEnabled;
            _editorEnabledStates.Clear();
            return;
        }
        _editorEnabledStates.Clear();
        DisableChildren(EditorContent);

        void DisableChildren(DependencyObject parent)
        {
            for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
                if (child is Control control)
                {
                    _editorEnabledStates.Add((control, control.IsEnabled));
                    control.IsEnabled = false;
                }
                else DisableChildren(child);
            }
        }
    }

    private void ShowEditorError(string message)
    {
        SendEditorErrorInfoBar.Message = message;
        SendEditorErrorInfoBar.Severity = InfoBarSeverity.Error;
        SendEditorErrorInfoBar.Visibility = Visibility.Visible;
        SendEditorErrorInfoBar.IsOpen = true;
    }
    private async void OnCancelEditorClick(object sender, RoutedEventArgs e)
    {
        if (_isSavingEditor) return;
        Interlocked.Increment(ref _selectionRequest);
        CloseEditor();
        if (_selectedItem is not null) await ShowDetailAsync(_selectedItem);
        else { ChooseSendHint.Visibility = Visibility.Visible; UpdateAdaptiveLayout(); }
    }
    private void CloseEditor()
    {
        if (_draft is not null) { _draft.PropertyChanged -= OnDraftChanged; _draft.ClearSensitive(); }
        _draft = null;
        DraftRow.Visibility = Visibility.Collapsed;
        DraftRowName.Text = string.Empty;
        _editingItem = null;
        _editorDirty = false;
        _editorBaseline = null;
        Draft = SendEditorDraft.CreateDefault(SendType.Text);
        Bindings.Update();
        EditorScroll.Visibility = Visibility.Collapsed;
    }
    private void ClearPageSensitive()
    {
        Interlocked.Increment(ref _selectionRequest);
        _editorSaveCts?.Cancel();
        CloseEditor();
        _detailDraft?.ClearSensitive();
        _detailDraft = null;
        _selectedItem = null;
        SelectedMoreButton.DataContext = null;
        SendsList.SelectedItem = null;
        ViewModel.SelectedMenuItem = null;
        SendTextContent.Text = string.Empty;
        DetailNotes.Text = string.Empty;
        SetShareLinkDisplay(null);
        DetailName.Text = string.Empty;
        DetailFileName.Text = string.Empty;
        DetailScroll.Visibility = Visibility.Collapsed;
        _copyTimer.Stop();
        _copiedLinkVersion = 0;
        ViewModel.CancelReceive();
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CopyLinkCommand.Execute(ItemFromSender(sender));
        if (ViewModel.HasCopyFeedback && sender is Button { Content: FontIcon icon } button)
        {
            icon.Glyph = "\uE73E";
            button.Tag = _clipboardStatus?.WriteVersion;
        }
    }
    private void OnCopyLinkMenuClick(object sender, RoutedEventArgs e) => ViewModel.CopyLinkCommand.Execute(ResolveItem(sender));
    private void OnCopySelectedLinkClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CopyLinkCommand.Execute(_selectedItem);
        if (ViewModel.HasCopyFeedback)
        {
            _copiedLinkVersion = _clipboardStatus?.WriteVersion ?? 0;
            ShareLinkLabel.Text = "✓ 已复制";
            UpdateLinkCopyFeedback();
            _copyTimer.Start();
        }
    }
    private void UpdateLinkCopyFeedback()
    {
        if (_clipboardStatus is null || _copiedLinkVersion == 0) return;
        if (!_clipboardStatus.HasContent || _clipboardStatus.WriteVersion != _copiedLinkVersion)
        {
            ShareLinkLabel.Text = "分享链接";
            ShareLinkProgress.Visibility = Visibility.Collapsed;
            _copyTimer.Stop();
            return;
        }
        if (_clipboardStatus.ClearAt is not { } clearAt)
        {
            ShareLinkLabel.Text = "✓ 已复制";
            ShareLinkProgress.Visibility = Visibility.Collapsed;
            return;
        }
        var seconds = Math.Max(0, (int)Math.Ceiling((clearAt - DateTimeOffset.UtcNow).TotalSeconds));
        ShareLinkLabel.Text = $"✓ 已复制 · {seconds} 秒";
        ShareLinkProgress.Visibility = Visibility.Visible;
        ShareLinkProgress.Value = Math.Clamp(seconds / (double)Math.Max(1, _clipboardStatus.TotalSeconds), 0, 1);
    }
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text) || !TryCaptureExpectedLease(out _)) return;
        try
        {
            var clipboard = global::App.App.Services.GetRequiredService<IClipboardService>();
            clipboard.SetSecretText(text);
            (clipboard as IClipboardStatus)?.SetLabel("文本");
            ViewModel.CopyFeedback = "文本已复制";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { ViewModel.Error = "无法复制文本，请稍后重试。"; }
    }
    private void OnCopyTextClick(object sender, RoutedEventArgs e) => CopyText(_detailDraft?.Text ?? _selectedItem?.TextContent);
    private void OnCopyReceivedTextClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ShowReceivedTextContent) CopyText(ViewModel.ReceivedText);
    }
    private void OnSelectedMoreClick(object sender, RoutedEventArgs e) => ViewModel.MarkMoreMenuOpened(_selectedItem);
    private void OnRowMenuOpening(object sender, object e) { }
    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e) => ViewModel.MarkMoreMenuOpened(ItemFromSender(sender));
    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("RowCopyButton") is Button button)
        {
            button.Visibility = Visibility.Visible;
            if (button.Content is FontIcon glyph)
                glyph.Glyph = button.Tag is long version && version == _clipboardStatus?.WriteVersion && _clipboardStatus?.HasContent == true ? "\uE73E" : "\uE71B";
        }
        if (sender is Grid row && row.FindName("RowProtection") is FontIcon icon) icon.Visibility = Visibility.Collapsed;
    }
    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("RowCopyButton") is Button button) button.Visibility = Visibility.Collapsed;
        if (sender is Grid row && row.FindName("RowProtection") is FontIcon icon) icon.Visibility = ItemFromSender(sender)?.HasRowStatus == true ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void OnToggleDisabledClick(object sender, RoutedEventArgs e)
    {
        var item = ResolveItem(sender);
        if (item is null || !ViewModel.CanModifySend(item) || !TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync() || !IsCurrentPageRequest(request)) return;
        var draft = await LoadDraftAsync(item.Id, request);
        if (draft is null) return;
        try
        {
            if (!IsCurrentPageRequest(request) || !request.Session.SameSessionAs(draft.OriginatingSession)) return;
            draft.Disabled = !draft.Disabled;
            var result = await ViewModel.UpdateSendFromDraftAsync(item, draft, null, request.Token);
            if (result.IsSuccess && ViewModel.LastCommittedItem is { } committed)
            {
                Task detailTask = Task.CompletedTask;
                if (TryCommitPageRequest(request, () =>
                {
                    ViewModel.CopyFeedback = committed.Disabled ? $"已禁用“{item.Name}”，链接暂时打不开" : $"已启用“{item.Name}”";
                    detailTask = ShowDetailAsync(committed);
                })) await detailTask;
            }
        }
        finally { draft.ClearSensitive(); }
    }
    private async void OnDeleteSendClick(object sender, RoutedEventArgs e) => await DeleteItemAsync(ResolveItem(sender));
    private async Task DeleteItemAsync(SendListItem? item)
    {
        if (item is null || !ViewModel.CanModifySend(item) || ViewModel.LoadedSession is not { } expectedSession
            || !TryBeginPageRequest(out var request)) return;
        if (!await DialogHelper.ConfirmAsync(XamlRoot, expectedSession, $"删除“{item.Name}”？", "链接将立即失效，分享的内容会永久删除，无法撤销。", "删除")) return;
        if (!IsCurrentPageRequest(request)) return;
        await ViewModel.DeleteSendCommand.ExecuteAsync(new SendDeleteRequest(item, expectedSession));
        if (ViewModel.Items.All(existing => existing.Id != item.Id))
        {
            TryCommitPageRequest(request, () =>
            {
            if (_selectedItem?.Id == item.Id) { _selectedItem = null; DetailScroll.Visibility = Visibility.Collapsed; ChooseSendHint.Visibility = Visibility.Visible; }
            ViewModel.CopyFeedback = $"已删除“{item.Name}”";
            SendsList.SelectedItem = ViewModel.FilteredItems.FirstOrDefault();
            });
        }
    }
    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SortByDeletion = sender is FrameworkElement { Tag: "deletion" };
        SortButton.Content = ViewModel.SortByDeletion ? "即将删除" : "最近修改";
        ReconcileFilteredSelection();
    }
    private async void OnEditAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (!CanHandleListShortcut()) return; args.Handled = true; await EditItemAsync(_selectedItem); }
    private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (_draft is null) return; args.Handled = true; OnSaveEditorClick(this, new RoutedEventArgs()); }
    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (!CanHandleListShortcut() || HasSelectedText(this)) return; args.Handled = true; OnCopySelectedLinkClick(this, new RoutedEventArgs()); }
    private async void OnDeleteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) { if (!CanHandleListShortcut()) return; args.Handled = true; await DeleteItemAsync(_selectedItem); }
    private async void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_draft is null || XamlRoot is null || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0) return;
        args.Handled = true;
        if (await CanNavigateAwayAsync()) OnCancelEditorClick(this, new RoutedEventArgs());
    }
    private bool CanHandleListShortcut()
    {
        if (_isReceive || _draft is not null || XamlRoot is null
            || Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0) return false;
        return FocusManager.GetFocusedElement(XamlRoot) is not (TextBox or PasswordBox or RichEditBox or ComboBox);
    }
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || !CanHandleListShortcut()
            || e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)) return;
        MoveSearchSelection(e.Key == Windows.System.VirtualKey.Up ? -1 : 1);
        e.Handled = true;
    }
    private static bool HasSelectedText(DependencyObject parent)
    {
        if (parent is TextBlock { SelectedText.Length: > 0 } or RichTextBlock { SelectedText.Length: > 0 }) return true;
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            if (HasSelectedText(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index))) return true;
        return false;
    }
    private void OnReceiveKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        OpenReceivedLink();
    }
    private void OnOpenReceivedClick(object sender, RoutedEventArgs e) => OpenReceivedLink();
    private void OpenReceivedLink()
    {
        if (ViewModel.IsBusy) return;
        if (string.IsNullOrWhiteSpace(ViewModel.ReceivedLinkUrl))
        {
            ViewModel.Error = "请先粘贴链接";
            ReceivedLinkTextBox.Focus(FocusState.Programmatic);
            return;
        }
        ViewModel.OpenReceivedLinkCommand.Execute(null);
    }
    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAdaptiveLayout();
    private void UpdateAdaptiveLayout()
    {
        if (SendSurface is null) return;
        var width = XamlRoot?.Size.Width ?? ActualWidth;
        var narrow = width > 0 && width < 800;
        var hasDetail = _draft is not null || _selectedItem is not null;
        SendListColumn.Width = new GridLength(narrow ? (hasDetail ? 0 : Math.Max(0, SendSurface.ActualWidth)) : width >= 1680 ? 380 : width < 1000 ? 300 : 344);
        ListPane.Visibility = narrow && hasDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = narrow && !hasDetail ? Visibility.Collapsed : Visibility.Visible;
        NarrowBackBar.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        DetailContent.Padding = new Thickness(narrow ? 24 : 40, 32, narrow ? 24 : 40, 36);
        EditorContent.Padding = new Thickness(narrow ? 24 : 40, 32, narrow ? 24 : 40, 40);
        UpdateEditorValueLayout();
    }
    private void UpdateEditorValueLayout()
    {
        if (EditorContent is null || EditorContent.ActualWidth <= 0) return;
        var available = EditorContent.ActualWidth - EditorContent.Padding.Left - EditorContent.Padding.Right - 128;
        PlaceHint(DeletionEditorHint, available < 430);
        PlaceHint(ExpirationEditorHint, available < 430);
        PlaceHint(EditorAccessHint, available < 290);
        PlaceHint(RemovePasswordButton, available < 400);
        SendPasswordBox.Width = Math.Clamp(available, 120, 280);
        var fileButtonBelow = available < 330;
        Grid.SetColumn(ChooseFileButton, fileButtonBelow ? 0 : 3);
        Grid.SetColumnSpan(ChooseFileButton, fileButtonBelow ? 4 : 1);
        Grid.SetRow(ChooseFileButton, fileButtonBelow ? 1 : 0);
        ChooseFileButton.HorizontalAlignment = HorizontalAlignment.Left;
        EditorFileName.MaxWidth = Math.Clamp(available - (fileButtonBelow ? 90 : 180), 60, 180);

        static void PlaceHint(FrameworkElement hint, bool below)
        {
            Grid.SetColumn(hint, below ? 0 : 1);
            Grid.SetRow(hint, below ? 1 : 0);
            Grid.SetColumnSpan(hint, below ? 2 : 1);
        }
    }
    private void UpdateEditorFileSummary()
    {
        if (_draft is null) return;
        var hasFile = !string.IsNullOrWhiteSpace(_draft.FileName);
        ChooseFileButton.Content = hasFile ? "更换文件…" : "选择文件…";
        EditorFileIcon.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        EditorFileSize.Text = hasFile ? FormatSize(_draft.ReplacementFileLength ?? _editingItem?.FileSize) : "最大 500 MB";
    }
    private async void OnBackToListClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync()) return;
        TryCommitPageRequest(request, () =>
        {
        CloseEditor();
        ResetSelectedDetail();
        UpdateAdaptiveLayout();
        });
    }
    private void OnDownloadOwnFileClick(object sender, RoutedEventArgs e)
    {
        if (_selectedItem?.Link is not { } link) return;
        SelectFilter("send:receive");
        ViewModel.ReceivedLinkUrl = link;
        ViewModel.CopyFeedback = "已填入文件链接。打开并验证后即可下载。";
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


}
