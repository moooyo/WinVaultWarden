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
    public event EventHandler? ContextChanged;
    public Visibility EmptyStateVisibility(bool isEmpty) => isEmpty && _draft is null ? Visibility.Visible : Visibility.Collapsed;
    public DateTimeOffset CustomDeletionMinDate => DateTimeOffset.Now;
    public DateTimeOffset CustomDeletionMaxDate => DateTimeOffset.Now.AddDays(31);
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
    private bool _narrowDetailVisible;
    private bool _changingSelection;
    private bool _initializingEditor;
    private bool _editorDirty;
    private EditorSnapshot? _editorBaseline;
    private bool _isSavingEditor;
    private readonly List<(Control Control, bool WasEnabled)> _editorEnabledStates = new();
    private readonly HashSet<Grid> _sendRows = new();
    private bool _sendRowSelectionRefreshQueued;
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
        MaxAccessTextBox.TextChanged += (_, _) =>
        {
            if (!_initializingEditor && _draft is not null) _editorDirty = true;
            UpdateEditorDirtyIndicator();
        };
        EditorContent.SizeChanged += (_, _) => UpdateEditorValueLayout();
        SendEditorErrorInfoBar.Closed += (_, _) => SendEditorErrorInfoBar.Visibility = Visibility.Collapsed;
        ActualThemeChanged += (_, _) =>
        {
            if (_selectedItem is { } item) UpdateStatusAction(item);
            if (_draft is not null) UpdateEditorDateHint();
        };
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
        var confirmed = await DialogHelper.ConfirmAsync(XamlRoot, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAEA9D6B5BDF48FF8),
            global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextBA6B12BAF2CCC5B8,(string.IsNullOrWhiteSpace(draft.Name) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3CD2814CDF6C7A1C) : draft.Name)), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEF804EDA908B10F1));
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

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        if (_draft is not null || _isSavingEditor) return;
        ApplySearch(string.Empty);
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSearchAllSendsClick(object sender, RoutedEventArgs e)
    {
        if (_draft is not null || _isSavingEditor) return;
        ViewModel.SelectFilterByTag("send:all");
        ReconcileFilteredSelection();
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReconcileFilteredSelection()
    {
        if (_draft is not null || _isReceive) return;
        var matching = ViewModel.FilteredItems.FirstOrDefault(item => item.Id == _selectedItem?.Id);
        if (matching is not null) { SetLoadedSelection(matching); return; }
        ResetSelectedDetail();
        if ((XamlRoot?.Size.Width ?? ActualWidth) >= 800)
            SendsList.SelectedItem = ViewModel.FilteredItems.FirstOrDefault();
        UpdateAdaptiveLayout();
    }

    private void SetLoadedSelection(SendListItem item)
    {
        if (_narrowDetailVisible || (XamlRoot?.Size.Width ?? ActualWidth) >= 800
            || _selectedItem?.Id != item.Id)
        {
            SendsList.SelectedItem = item;
            return;
        }

        var refreshDetail = DetailScroll.Visibility != Visibility.Visible
            || (_selectedItem is { } selected && (item with { DisplayTime = selected.DisplayTime }) != selected);
        _changingSelection = true;
        try
        {
            _selectedItem = item;
            SendsList.SelectedItem = item;
        }
        finally
        {
            _changingSelection = false;
        }
        RefreshSendRowSelectionIndicators();
        if (refreshDetail)
            _ = RefreshSelectedDetailAsync(item, showNarrowDetail: false);
    }

    private void ResetSelectedDetail()
    {
        Interlocked.Increment(ref _selectionRequest);
        _narrowDetailVisible = false;
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
        if (SendsList.SelectedItem is not SendListItem item)
            SendsList.SelectedIndex = 0;
        else if (!_narrowDetailVisible)
            _ = ReopenSelectedDetailAsync(item);
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
            if (item is not null)
                SetLoadedSelection(item);
            else
            {
                ResetSelectedDetail();
                UpdateAdaptiveLayout();
            }
        });
    }

    private static SendListItem? ItemFromSender(object sender) =>
        sender is FrameworkElement { DataContext: SendListItem item } ? item : null;

    private SendListItem? ResolveItem(object sender) =>
        ItemFromSender(sender) ?? ViewModel.SelectedMenuItem ?? _selectedItem;

    private void OnAddSendClick(object sender, RoutedEventArgs e) => BeginCreate();
    private void OnRetryClick(object sender, RoutedEventArgs e) => RefreshSendList();

    private async void OnSendItemClick(object sender, ItemClickEventArgs e)
    {
        if (_narrowDetailVisible || e.ClickedItem is not SendListItem item || item.Id != _selectedItem?.Id) return;
        await ReopenSelectedDetailAsync(item);
    }

    private Task ReopenSelectedDetailAsync(SendListItem item) =>
        RefreshSelectedDetailAsync(item, showNarrowDetail: true);

    private async Task RefreshSelectedDetailAsync(SendListItem item, bool showNarrowDetail)
    {
        if (_isReceive || _draft is not null || !TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync()) return;
        Task detailTask = Task.CompletedTask;
        if (TryCommitPageRequest(request, () => detailTask = ShowDetailAsync(item, showNarrowDetail)))
            await detailTask;
    }

    private async void OnSendSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSendRowSelectionIndicators();
        QueueSendRowSelectionRefresh();
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

    private async Task ShowDetailAsync(SendListItem item, bool showNarrowDetail = true)
    {
        if (!TryBeginPageRequest(out var request)) return;
        _narrowDetailVisible = showNarrowDetail;
        _selectedItem = item;
        ViewModel.SelectedMenuItem = null;
        _detailDraft?.ClearSensitive();
        _detailDraft = null;
        DetailName.Text = item.Name;
        DetailGlyph.Glyph = item.Glyph;
        DetailSubtitle.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text7FE4A03629B8D81B,item.TypeLabel);
        DetailStatus.Text = StatusText(item);
        UpdateStatusAction(item);
        EditSelectedSendButton.IsEnabled = ViewModel.CanModifySend(item);
        SelectedMoreButton.DataContext = item;
        SetShareLinkDisplay(item.Link);
        ShareLinkLabel.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text10BA647975FEB6FF);
        SetSelectedLinkCopyState(false);
        CopySelectedLinkButton.IsEnabled = !string.IsNullOrEmpty(item.Link);
        ShareLinkProgress.Visibility = Visibility.Collapsed;
        _copiedLinkVersion = 0;
        DetailDeletion.Text = FormatDate(item.DeletionDateTime);
        DetailDeletionHint.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text617D95815D89849C,item.DeletionRelativeLabel);
        DetailExpiration.Text = item.ExpirationDate is { } expiration ? FormatDate(expiration) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2C60316D5EA6B49C);
        DetailAccessLimit.Text = item.MaxAccessCount is { } max ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text2AA79928FCE832D1,max) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF203D577D14268CD);
        DetailAccessCount.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text74F5BA738617CBDD,item.AccessCount);
        DetailPassword.Text = item.HasPassword ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8F0B7BD98A6A172F) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2F5F1D6FBFB061ED);
        DetailHideEmail.Text = "—";
        SendTextContent.Text = item.TextContent ?? string.Empty;
        DetailFileName.Text = item.FileName ?? item.Name;
        DetailFileSize.Text = FormatSize(item.FileSize);
        DetailModified.Text = item.RevisionDate is { } revision ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text30D233A280B1A3C6,FormatDate(revision)) : string.Empty;
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
                    DetailHideEmail.Text = detail.HideEmail ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text19213DA910F2DFA3) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0C70665B6EB65F1A);
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
            TryCommitPageRequest(request, () => ViewModel.Error = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text803062EAD47A99E1));
        }
    }

    private static string FormatDate(DateTimeOffset? date) =>
        date is { } value ? value.ToLocalTime().ToString("g", global::App.Localization.UiText.DisplayCulture)
            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7EE0F1015D52DB6B);

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
            SendAvailabilityState.Available => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text165577EC1B033A30),
            SendAvailabilityState.Disabled => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0BB46B05BDE0113F),
            SendAvailabilityState.Expired => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text014B96AD483A1FFA,FormatDate(item.ExpirationDate)),
            SendAvailabilityState.Deleted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDCB9E19FE323DCD1),
            SendAvailabilityState.AccessLimitReached => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF3508BE4CF2C7311),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDBA89EC86A14EB9E),
        };
        return global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text9E66D2A91374ECC9,state,item.DeletionRelativeLabel,item.AccessCount,(item.HasPassword ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0A6813F5831C2823) : string.Empty));
    }

    private void UpdateStatusAction(SendListItem item)
    {
        var state = item.GetAvailability(DateTimeOffset.UtcNow).State;
        var label = state switch
        {
            SendAvailabilityState.Disabled => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF4F0EAD1116B5B62),
            SendAvailabilityState.Expired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE0B9A93D3DF07D42),
            SendAvailabilityState.AccessLimitReached => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18B347D43C4B46F0),
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
                    ? UiExceptionPolicy.RequestTimeoutMessage : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text803062EAD47A99E1);
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
        SaveEditorButton.Content = item is null ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA71BF6DF75763893) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA3030BF8F16DC63C);
        ToolTipService.SetToolTip(SaveEditorButton, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text00D331A49C8881FB,SaveEditorButton.Content));
        SendPasswordBox.PlaceholderText = draft.ExistingHasPassword ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE4C47C23589FEC42) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text537352400C495FB8);
        UpdatePasswordActions();
        MaxAccessTextBox.Text = draft.MaxAccessCount?.ToString() ?? string.Empty;
        EditorAccessHint.Text = item is null ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3354EE1CF1F1D402) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextAFD0C28C8D300E77,item.AccessCount);
        EditorTypeRow.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateEditorFileSummary();
        FillDateChoices(draft, item is not null);
        UpdateEditorType();
        UpdateDraftRow();
        _initializingEditor = false;
        _editorDirty = false;
        _editorBaseline = CaptureEditorSnapshot();
        UpdateEditorDirtyIndicator();
        EditorNameError.Visibility = Visibility.Collapsed;
        EditorTextError.Visibility = Visibility.Collapsed;
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
        FillDateChoices(DeletionChoice, SendDatePresetOptions.CreateDeletionOptions(editing, draft.DeletionDate),
            editing ? SendDatePreset.KeepExisting : draft.DeletionPreset);
        CustomDeletionEditorPanel.Visibility = Visibility.Collapsed;
        var keepExpiration = editing && draft.ExpirationDate is not null;
        FillDateChoices(ExpirationChoice, SendDatePresetOptions.CreateExpirationOptions(keepExpiration, draft.ExpirationDate),
            keepExpiration ? SendDatePreset.KeepExisting : draft.ExpirationPreset);
        CustomExpirationEditorPanel.Visibility = Visibility.Collapsed;
        UpdateEditorDateHint();
    }

    private static void FillDateChoices(
        ComboBox combo,
        IReadOnlyList<SendDatePresetOption> options,
        SendDatePreset selected)
    {
        combo.Items.Clear();
        foreach (var option in options)
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Value };
            combo.Items.Add(item);
            if (option.Value == selected)
                combo.SelectedItem = item;
        }
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_initializingEditor) _editorDirty = true;
        if (e.PropertyName is nameof(SendEditorDraft.Name) or nameof(SendEditorDraft.Type)) UpdateDraftRow();
        if (e.PropertyName is nameof(SendEditorDraft.Password) or nameof(SendEditorDraft.RemovePassword))
            UpdatePasswordActions();
        if (e.PropertyName is nameof(SendEditorDraft.DeletionDate) or nameof(SendEditorDraft.DeletionDateLabel) or nameof(SendEditorDraft.ExpirationDate))
            UpdateEditorDateHint();
        if (e.PropertyName == nameof(SendEditorDraft.Name) && !string.IsNullOrWhiteSpace(_draft?.Name))
            EditorNameError.Visibility = Visibility.Collapsed;
        if (e.PropertyName == nameof(SendEditorDraft.Text) && !string.IsNullOrWhiteSpace(_draft?.Text))
            EditorTextError.Visibility = Visibility.Collapsed;
        UpdateEditorDirtyIndicator();
    }

    private void UpdateEditorDirtyIndicator() =>
        EditorDirtyIndicator.Visibility = _draft is not null && _editorDirty && CaptureEditorSnapshot() != _editorBaseline
            ? Visibility.Visible : Visibility.Collapsed;

    private void UpdatePasswordActions()
    {
        var hasExistingPassword = _draft is { ExistingHasPassword: true } draft && string.IsNullOrEmpty(draft.Password);
        var removing = hasExistingPassword && _draft!.RemovePassword;
        PasswordActionGroup.Visibility = hasExistingPassword ? Visibility.Visible : Visibility.Collapsed;
        RemovePasswordButton.Visibility = hasExistingPassword && !removing ? Visibility.Visible : Visibility.Collapsed;
        RemovePasswordSummary.Visibility = removing ? Visibility.Visible : Visibility.Collapsed;
        UndoRemovePasswordButton.Visibility = removing ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnRemovePasswordClick(object sender, RoutedEventArgs e)
    {
        if (_draft is null) return;
        _draft.RemovePassword = !_draft.RemovePassword;
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
        RefreshSendRowSelectionIndicators();
        if (_draft is null) return;
        DraftRowName.Text = string.IsNullOrWhiteSpace(_draft.Name)
            ? _draft.IsFile ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA3EB22D84F84156C) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text80774221469D4826) : _draft.Name;
        DraftRowGlyph.Glyph = _draft.IsFile ? "\uE8A5" : "\uE8E4";
    }
    private void OnDeletionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingEditor || _isSavingEditor || _draft is null
            || DeletionChoice.SelectedItem is not ComboBoxItem { Tag: SendDatePreset preset }) return;
        CustomDeletionEditorPanel.Visibility = preset == SendDatePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        if (preset == SendDatePreset.KeepExisting)
        {
            if (_editorBaseline is { } baseline)
            {
                _draft.DeletionPreset = baseline.DeletionPreset;
                _draft.DeletionDate = baseline.DeletionDate;
            }
            return;
        }
        _draft.SelectDeletion(preset, DateTimeOffset.UtcNow);
    }
    private void OnExpirationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingEditor || _isSavingEditor || _draft is null
            || ExpirationChoice.SelectedItem is not ComboBoxItem { Tag: SendDatePreset preset }) return;
        CustomExpirationEditorPanel.Visibility = preset == SendDatePreset.Custom ? Visibility.Visible : Visibility.Collapsed;
        if (preset == SendDatePreset.KeepExisting)
        {
            if (_editorBaseline is { } baseline) _draft.ExpirationDate = baseline.ExpirationDate;
            return;
        }
        _draft.SelectExpiration(preset, DateTimeOffset.UtcNow);
    }

    private void OnCustomDeletionDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_initializingEditor || _isSavingEditor || _draft is null
            || CustomDeletionEditorPanel.Visibility != Visibility.Visible || args.NewDate is not { } selectedDate) return;
        _draft.SelectCustomDeletionDate(selectedDate, TimeZoneInfo.Local);
    }

    private void OnCustomExpirationDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_initializingEditor || _isSavingEditor || _draft is null
            || CustomExpirationEditorPanel.Visibility != Visibility.Visible || args.NewDate is not { } selectedDate) return;
        _draft.SelectCustomExpirationDate(selectedDate, TimeZoneInfo.Local);
    }

    private void UpdateEditorDateHint()
    {
        var invalid = _draft is { ExpirationDate: { } expiration } draft && expiration > draft.ToDeletionDate();
        ExpirationEditorHint.Text = invalid ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text55EE8047070A7ED5) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE01A80721B7B6FE5);
        ExpirationEditorHint.Foreground = ThemeBrushResolver.Resolve(this,
            invalid ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
    }

    private EditorSnapshot? CaptureEditorSnapshot() => _draft is not { } draft ? null : new(
        draft.Type, draft.Name, draft.Text, draft.FileName, draft.Notes, draft.Password,
        draft.HideTextByDefault, draft.HideEmail, draft.Disabled, draft.RemovePassword,
        int.TryParse(MaxAccessTextBox.Text, out var count) ? count : null,
        draft.DeletionPreset, draft.DeletionDate, draft.ExpirationDate, draft.ReplacementFile);

    private sealed record EditorSnapshot(
        SendType Type, string Name, string Text, string FileName, string Notes, string Password,
        bool HideText, bool HideEmail, bool Disabled, bool RemovePassword, int? MaxAccessCount,
        SendDatePreset DeletionPreset, DateTimeOffset? DeletionDate, DateTimeOffset? ExpirationDate,
        Core.Models.SendFileSource? ReplacementFile);
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
            ChooseFileButton.Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC28BBBFA516EBE4F);
            UpdateEditorFileSummary();
            _editorDirty = true;
            if (_editingItem is not null)
            {
                SendEditorErrorInfoBar.Severity = InfoBarSeverity.Warning;
                SendEditorErrorInfoBar.Message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBDBC0F991F8213EB);
                SendEditorErrorInfoBar.Visibility = Visibility.Visible;
                SendEditorErrorInfoBar.IsOpen = true;
                if (draft.ExistingHasPassword && !draft.RemovePassword)
                    SendPasswordBox.PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text411F775524FE9C8B);
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, _pageLifetimeCts?.Token ?? default)) { }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
        { ShowEditorError(ex is ArgumentOutOfRangeException ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFC35EAAAAD04F005) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text51BDBEFBC0C88421)); }
    }

    private async void OnSaveEditorClick(object sender, RoutedEventArgs e)
    {
        var draft = _draft;
        var item = _editingItem;
        if (draft is null || _isSavingEditor) return;
        if (string.IsNullOrWhiteSpace(draft.Name)) { EditorNameError.Visibility = Visibility.Visible; ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFBC79033BFA23D52)); SendNameTextBox.Focus(FocusState.Programmatic); return; }
        if (draft.IsText && string.IsNullOrWhiteSpace(draft.Text)) { EditorTextError.Visibility = Visibility.Visible; ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5490F72FC3FC3B4B)); SendTextBox.Focus(FocusState.Programmatic); return; }
        if (draft.IsFile && (string.IsNullOrWhiteSpace(draft.FileName) || (item is null && draft.ReplacementFile is null))) { ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text86A22F01B362998D)); return; }
        if (draft.ExpirationDate is { } expiration && expiration > draft.ToDeletionDate())
        {
            ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text55EE8047070A7ED5));
            ExpirationChoice.Focus(FocusState.Programmatic);
            return;
        }
        var replacingFile = item is not null && draft.IsFile && draft.ReplacementFile is not null;
        if (replacingFile && draft.ExistingHasPassword && !draft.RemovePassword && string.IsNullOrWhiteSpace(draft.Password))
        {
            ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6BC108E1C3299364));
            SendPasswordBox.Focus(FocusState.Programmatic);
            return;
        }
        draft.MaxAccessCount = int.TryParse(MaxAccessTextBox.Text, out var limit) ? limit : null;
        _isSavingEditor = true;
        SetEditorBusy(true);
        SaveEditorButton.Content = item is null ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C0305BC0CE607B2) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6BDB4435095E5D28);
        using var saveCts = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCts?.Token ?? default);
        _editorSaveCts = saveCts;
        try
        {
            if (replacingFile)
            {
                var passwordMessage = draft.ExistingHasPassword && draft.RemovePassword
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text41F77B20297C7856) : string.Empty;
                if (draft.OriginatingSession is not { } expectedSession
                    || !await DialogHelper.ConfirmAsync(XamlRoot, expectedSession, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7B4EB5FD002106F1),
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBCC4B47B810D5602) + passwordMessage,
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEAA8249AD98560BB))
                    || !ReferenceEquals(draft, _draft)
                    || saveCts.IsCancellationRequested)
                    return;
            }
            var result = item is null
                ? await ViewModel.CreateSendAsync(draft, draft.ReplacementFile, saveCts.Token)
                : await ViewModel.UpdateSendFromDraftAsync(item, draft, draft.ReplacementFile, saveCts.Token);
            if (!ReferenceEquals(draft, _draft)) return;
            if (!result.IsSuccess) { if (result.Status == UiOperationStatus.Failed) ShowEditorError(result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8715DD8F2827D9A3)); return; }
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
                    if (ViewModel.HasCopyFeedback) ViewModel.CopyFeedback = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text00A26BDA6B54914B,name);
                }
                else if (replacingFile && committed.ReconciliationState == SendReconciliationState.None)
                {
                    ViewModel.CopyLinkCommand.Execute(committed);
                    if (ViewModel.HasCopyFeedback)
                        ViewModel.CopyFeedback = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA418CDF8F9730169);
                }
                else if (committed.ReconciliationState == SendReconciliationState.None)
                    ViewModel.CopyFeedback = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextDFBFD1445B27E2C2,name);
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
            SaveEditorButton.Content = _editingItem is null ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA71BF6DF75763893) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA3030BF8F16DC63C);
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
        EditorDirtyIndicator.Visibility = Visibility.Collapsed;
        CustomDeletionEditorPanel.Visibility = Visibility.Collapsed;
        CustomExpirationEditorPanel.Visibility = Visibility.Collapsed;
        Draft = SendEditorDraft.CreateDefault(SendType.Text);
        Bindings.Update();
        EditorScroll.Visibility = Visibility.Collapsed;
        RefreshSendRowSelectionIndicators();
    }
    private void ClearPageSensitive()
    {
        Interlocked.Increment(ref _selectionRequest);
        _editorSaveCts?.Cancel();
        CloseEditor();
        _detailDraft?.ClearSensitive();
        _detailDraft = null;
        _narrowDetailVisible = false;
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
        SetSelectedLinkCopyState(false);
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
            ShareLinkLabel.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBC860F5A90103162);
            SetSelectedLinkCopyState(true);
            UpdateLinkCopyFeedback();
            _copyTimer.Start();
        }
    }
    private void UpdateLinkCopyFeedback()
    {
        if (_clipboardStatus is null || _copiedLinkVersion == 0) return;
        if (!_clipboardStatus.HasContent || _clipboardStatus.WriteVersion != _copiedLinkVersion)
        {
            ShareLinkLabel.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text10BA647975FEB6FF);
            ShareLinkProgress.Visibility = Visibility.Collapsed;
            SetSelectedLinkCopyState(false);
            _copyTimer.Stop();
            return;
        }
        if (_clipboardStatus.ClearAt is not { } clearAt)
        {
            ShareLinkLabel.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBC860F5A90103162);
            ShareLinkProgress.Visibility = Visibility.Collapsed;
            return;
        }
        var seconds = Math.Max(0, (int)Math.Ceiling((clearAt - DateTimeOffset.UtcNow).TotalSeconds));
        ShareLinkLabel.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextD906CFE40897E21B,seconds);
        ShareLinkProgress.Visibility = Visibility.Visible;
        ShareLinkProgress.Value = Math.Clamp(seconds / (double)Math.Max(1, _clipboardStatus.TotalSeconds), 0, 1);
    }
    private void SetSelectedLinkCopyState(bool copied)
    {
        CopySelectedLinkGlyph.Glyph = copied ? "\uE73E" : "\uE71B";
        CopySelectedLinkText.Text = copied ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8F6F8D979C981CED) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8E86F9B1D54F2C51);
    }
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text) || !TryCaptureExpectedLease(out _)) return;
        try
        {
            var clipboard = global::App.App.Services.GetRequiredService<IClipboardService>();
            clipboard.SetSecretText(text);
            (clipboard as IClipboardStatus)?.SetLabel(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDD81861811E57BF7));
            ViewModel.CopyFeedback = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text714207E324062C61);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { ViewModel.Error = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB231003B3F9267B2); }
    }
    private void OnCopyTextClick(object sender, RoutedEventArgs e) => CopyText(_detailDraft?.Text ?? _selectedItem?.TextContent);
    private void OnCopyReceivedTextClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ShowReceivedTextContent) CopyText(ViewModel.ReceivedText);
    }
    private void OnSelectedMoreClick(object sender, RoutedEventArgs e) => ViewModel.MarkMoreMenuOpened(_selectedItem);
    private void OnRowMenuOpening(object sender, object e) { }
    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e) => ViewModel.MarkMoreMenuOpened(ItemFromSender(sender));
    private void OnSendRowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid row) return;
        _sendRows.Add(row);
        UpdateSendRowSelectionIndicator(row);
        QueueSendRowSelectionRefresh();
    }

    private void OnSendRowUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Grid row) return;
        _sendRows.Remove(row);
        UpdateSendRowSelectionIndicator(row);
    }

    private void OnSendRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not Grid row) return;
        UpdateSendRowSelectionIndicator(row);
        QueueSendRowSelectionRefresh();
    }

    private void OnSendContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var row = args.ItemContainer.ContentTemplateRoot as Grid;
        if (args.InRecycleQueue)
        {
            if (row is not null)
            {
                _sendRows.Remove(row);
                UpdateSendRowSelectionIndicator(row);
            }
            return;
        }

        if (row is not null)
        {
            if (row.IsLoaded) _sendRows.Add(row);
            UpdateSendRowSelectionIndicator(row);
        }
        if (args.Phase == 0)
            args.RegisterUpdateCallback(1, OnSendContainerContentChanging);
        QueueSendRowSelectionRefresh();
    }

    private void UpdateSendRowSelectionIndicator(Grid row)
    {
        if (row.FindName("RowSelectionIndicator") is not Border indicator) return;
        var selected = _sendRows.Contains(row) && row.IsLoaded
            && !(_draft is not null && _editingItem is null)
            && SendsList.SelectedItem is SendListItem selectedItem
            && row.DataContext is SendListItem item && item.Id == selectedItem.Id
            && SendsList.ContainerFromItem(item) is ListViewItem container && container.IsSelected
            && ReferenceEquals(container.ContentTemplateRoot, row);
        indicator.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshSendRowSelectionIndicators()
    {
        foreach (var row in _sendRows)
            UpdateSendRowSelectionIndicator(row);
    }

    private void QueueSendRowSelectionRefresh()
    {
        if (_sendRowSelectionRefreshQueued) return;
        _sendRowSelectionRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _sendRowSelectionRefreshQueued = false;
            RefreshSendRowSelectionIndicators();
        });
    }

    private void OnRowProtectionLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement row && ItemFromSender(sender) is { } item
            && row.FindName("RowAvailabilityIcon") is FontIcon icon)
            icon.Visibility = item.GetAvailability(item.DisplayTime).State is SendAvailabilityState.Available or SendAvailabilityState.Unknown
                ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("RowCopyButton") is Button button)
        {
            button.Visibility = Visibility.Visible;
            if (button.Content is FontIcon glyph)
                glyph.Glyph = button.Tag is long version && version == _clipboardStatus?.WriteVersion && _clipboardStatus?.HasContent == true ? "\uE73E" : "\uE71B";
        }
        if (sender is Grid row && row.FindName("RowProtection") is FrameworkElement status) status.Visibility = Visibility.Collapsed;
    }
    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("RowCopyButton") is Button button) button.Visibility = Visibility.Collapsed;
        if (sender is Grid row && row.FindName("RowProtection") is FrameworkElement status) status.Visibility = ItemFromSender(sender)?.HasRowStatus == true ? Visibility.Visible : Visibility.Collapsed;
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
                    ViewModel.CopyFeedback = committed.Disabled ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextA99EAF37CF61D865,item.Name) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text9CBF4D3457ACC26C,item.Name);
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
        if (!await DialogHelper.ConfirmAsync(XamlRoot, expectedSession, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text591F8EBAE3D1A223,item.Name), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBF402D7E00319243), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2F9DAA828907B93F))) return;
        if (!IsCurrentPageRequest(request)) return;
        await ViewModel.DeleteSendCommand.ExecuteAsync(new SendDeleteRequest(item, expectedSession));
        if (ViewModel.Items.All(existing => existing.Id != item.Id))
        {
            TryCommitPageRequest(request, () =>
            {
            if (_selectedItem?.Id == item.Id) { _selectedItem = null; DetailScroll.Visibility = Visibility.Collapsed; ChooseSendHint.Visibility = Visibility.Visible; }
            ViewModel.CopyFeedback = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text85E9EF438B3B0D24,item.Name);
            SendsList.SelectedItem = ViewModel.FilteredItems.FirstOrDefault();
            });
        }
    }
    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SortByDeletion = sender is FrameworkElement { Tag: "deletion" };
        SortButton.Content = ViewModel.SortByDeletion ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF5C342DCC3A751DA) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAD3BF1EEA84B70E4);
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
    private void OnSendListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != Windows.System.VirtualKey.Enter || !CanHandleListShortcut()
            || FocusManager.GetFocusedElement(XamlRoot) is not (ListView or ListViewItem)) return;
        AcceptSearchResult();
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
            ViewModel.Error = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE5BD7D078C8EA770);
            ReceivedLinkTextBox.Focus(FocusState.Programmatic);
            return;
        }
        ViewModel.OpenReceivedLinkCommand.Execute(null);
    }
    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => UpdateAdaptiveLayout();
    private void OnEditorHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 680;
        Grid.SetColumnSpan(EditorNamePanel, stacked ? 2 : 1);
        Grid.SetRow(EditorHeaderActions, stacked ? 1 : 0);
        Grid.SetColumn(EditorHeaderActions, stacked ? 0 : 2);
        Grid.SetColumnSpan(EditorHeaderActions, stacked ? 3 : 1);
    }

    private void UpdateAdaptiveLayout()
    {
        if (SendSurface is null) return;
        var width = XamlRoot?.Size.Width ?? ActualWidth;
        var narrow = width > 0 && width < 800;
        var hasDetail = _draft is not null || (_selectedItem is not null && _narrowDetailVisible);
        SendListColumn.Width = new GridLength(narrow ? (hasDetail ? 0 : Math.Max(0, SendSurface.ActualWidth)) : width >= 1680 ? 380 : width < 1000 ? 300 : 344);
        ListPane.Visibility = narrow && hasDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = narrow && !hasDetail ? Visibility.Collapsed : Visibility.Visible;
        NarrowBackBar.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        var contentWidth = Math.Clamp(SendSurface.ActualWidth - (narrow ? 0 : SendListColumn.Width.Value), 0, 1000);
        DetailContent.Width = contentWidth;
        EditorContent.Width = contentWidth;
        UpdateEditorValueLayout();
    }
    private void UpdateEditorValueLayout()
    {
        if (EditorContent is null || EditorContent.ActualWidth <= 0) return;
        var available = EditorContent.ActualWidth - EditorContent.Padding.Left - EditorContent.Padding.Right - 128;
        PlaceHint(DeletionEditorHint, available < 430);
        PlaceHint(ExpirationEditorHint, available < 430);
        PlaceHint(EditorAccessHint, available < 290);
        PlaceHint(PasswordActionGroup, available < 400);
        SendPasswordBox.Width = Math.Clamp(available, 120, 280);
        var hasFile = !string.IsNullOrWhiteSpace(_draft?.FileName);
        var fileButtonBelow = hasFile && available < 330;
        var fileHintBelow = !hasFile && available < 220;
        Grid.SetColumn(ChooseFileButton, hasFile && !fileButtonBelow ? 3 : 0);
        Grid.SetColumnSpan(ChooseFileButton, fileButtonBelow ? 4 : 1);
        Grid.SetRow(ChooseFileButton, fileButtonBelow ? 1 : 0);
        ChooseFileButton.Margin = new Thickness(0, fileButtonBelow ? 6 : 0, 0, 0);
        ChooseFileButton.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(EditorFileSize, hasFile ? 2 : fileHintBelow ? 0 : 1);
        Grid.SetColumnSpan(EditorFileSize, hasFile ? 1 : fileHintBelow ? 4 : 3);
        Grid.SetRow(EditorFileSize, fileHintBelow ? 1 : 0);
        EditorFileSize.Margin = new Thickness(0, fileHintBelow ? 6 : 0, 0, 0);
        EditorFileName.MaxWidth = Math.Clamp(available - (fileButtonBelow ? 90 : 180), 60, 180);

        static void PlaceHint(FrameworkElement hint, bool below)
        {
            Grid.SetColumn(hint, below ? 0 : 1);
            Grid.SetRow(hint, below ? 1 : 0);
            Grid.SetColumnSpan(hint, below ? 2 : 1);
            hint.Margin = new Thickness(0, below ? 6 : 0, 0, 0);
        }
    }
    private void UpdateEditorFileSummary()
    {
        if (_draft is null) return;
        var hasFile = !string.IsNullOrWhiteSpace(_draft.FileName);
        ChooseFileButton.Content = hasFile ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC28BBBFA516EBE4F) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text29D75B8EB8866BB4);
        EditorFileIcon.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        EditorFileName.Visibility = hasFile ? Visibility.Visible : Visibility.Collapsed;
        EditorFileSize.Text = hasFile ? FormatSize(_draft.ReplacementFileLength ?? _editingItem?.FileSize) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBA312869EDE848F7);
        UpdateEditorValueLayout();
    }
    private async void OnBackToListClick(object sender, RoutedEventArgs e)
    {
        if (!TryBeginPageRequest(out var request)) return;
        if (!await CanNavigateAwayAsync()) return;
        TryCommitPageRequest(request, () =>
        {
            CloseEditor();
            _narrowDetailVisible = false;
            var selected = ViewModel.FilteredItems.FirstOrDefault(item => item.Id == _selectedItem?.Id);
            if (selected is not null)
                SetLoadedSelection(selected);
            else
                ResetSelectedDetail();
            UpdateAdaptiveLayout();
            var focusRequest = request with { Generation = Volatile.Read(ref _selectionRequest) };
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_narrowDetailVisible || _draft is not null || _isReceive) return;
                TryCommitPageRequest(focusRequest, () =>
                {
                    if (SendsList.SelectedItem is { } item)
                    {
                        SendsList.ScrollIntoView(item);
                        if (SendsList.ContainerFromItem(item) is Control row && row.Focus(FocusState.Programmatic)) return;
                    }
                    SendsList.Focus(FocusState.Programmatic);
                });
            });
        });
    }
    private void OnDownloadOwnFileClick(object sender, RoutedEventArgs e)
    {
        if (_selectedItem?.Link is not { } link) return;
        SelectFilter("send:receive");
        ViewModel.ReceivedLinkUrl = link;
        ViewModel.CopyFeedback = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text56DD41C08C715C7F);
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
                picker.FileTypeChoices.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text39932F24FE11A6BA), new System.Collections.Generic.List<string> { "." });
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
            return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2019E7CD5801502A);

        return exception switch
        {
            UnauthorizedAccessException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8BACF90B63A4C664),
            InvalidDataException or System.Security.Cryptography.CryptographicException =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF3039FD9EA3627AA),
            HttpRequestException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBD573FBAE00C37F5),
            IOException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1A46F15D1AE566A7),
            ObjectDisposedException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA6A6B2306CD940BF),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF10034AFD900EE5E),
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
