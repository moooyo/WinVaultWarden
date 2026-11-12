using System.Collections.ObjectModel;
using Api;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;
using Core.Services;
using Core.Session;

namespace App.ViewModels;

public partial class SendViewModel : ObservableObject, ISensitiveState
{
    private readonly ISendUiService _service;
    private readonly IClipboardService? _clipboard;
    private readonly Vault.VaultSession? _session;
    private readonly IAppLifecycleCoordinator? _lifecycle;
    // 写操作(创建/更新)单持有者闸门:避免重入并发写。加载状态(LoadState)独立于写 busy(IsBusy)。
    private readonly AsyncOperationGate _writeGate = new();
    // latest-wins 加载:每次 LoadAsync 递增 requestId 并取消上一个 CTS;只有最新请求能提交集合与四态。
    private CancellationTokenSource? _loadCts;
    private long _loadRequestId;
    private readonly object _receiveGate = new();
    private CancellationTokenSource? _receiveCts;
    private long _receiveRequestId;
    private long _stateEpoch;

    public ObservableCollection<SendListItem> Items { get; } = new();
    public ObservableCollection<SendListItem> FilteredItems { get; } = new();
    public VaultSessionLease? LoadedSession { get; private set; }
    public SendListItem? LastCommittedItem { get; private set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool SortByDeletion { get; set; }

    public string ListTitle => SelectedFilterTag switch
    {
        "send:text" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDFF2BA37F1CE5081),
        "send:file" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text285713F32C4C8DB3),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text08F06E58676001CE),
    };
    public string ListCountLabel => $"{FilteredItems.Count}";
    public string EmptyTitle => !string.IsNullOrWhiteSpace(SearchQuery)
        ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text5DC5C08ECE0671A5,SearchQuery.Trim())
        : SelectedFilterTag switch
        {
            "send:text" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4EBAA1266C4751FE),
            "send:file" => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCD4AA0A045838BDF),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0C9D4E8D96CA1F1C),
        };
    public string EmptyHint => !string.IsNullOrWhiteSpace(SearchQuery)
        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDB87EBF6DE6B6BA6)
        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6F466A68943632D8);
    public bool ShowSearchEmptyActions => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool ShowSearchAllEmptyAction => ShowSearchEmptyActions && SelectedFilterTag != "send:all";
    public bool ShowCreateEmptyAction => !ShowSearchEmptyActions;

    [ObservableProperty]
    public partial string SelectedFilterTag { get; set; } = "send:all";

    [ObservableProperty]
    public partial SendListItem? SelectedMenuItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateSend))]
    [NotifyPropertyChangedFor(nameof(CanReceiveInput))]
    [NotifyPropertyChangedFor(nameof(ReceiveButtonText))]
    [NotifyCanExecuteChangedFor(nameof(OpenReceivedLinkCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateSend))]
    [NotifyPropertyChangedFor(nameof(ShowStaleInfo))]
    public partial bool IsReadOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    public partial DataPageState LoadState { get; set; } = DataPageState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotice))]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyFeedback))]
    public partial string? CopyFeedback { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReceivedLinkCommand))]
    public partial string ReceivedLinkUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReceivedLinkPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceivedText))]
    [NotifyPropertyChangedFor(nameof(ShowReceivedTextContent))]
    [NotifyPropertyChangedFor(nameof(HasHiddenReceivedText))]
    [NotifyPropertyChangedFor(nameof(DisplayedReceivedText))]
    public partial string? ReceivedText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReceivedTextContent))]
    [NotifyPropertyChangedFor(nameof(HasHiddenReceivedText))]
    [NotifyPropertyChangedFor(nameof(DisplayedReceivedText))]
    public partial bool ReceivedTextRevealed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceivedFile))]
    public partial string? ReceivedFileName { get; set; }

    [ObservableProperty]
    public partial bool ReceivedWrongPassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceiveButtonText))]
    [NotifyPropertyChangedFor(nameof(ReceivedSourceText))]
    [NotifyPropertyChangedFor(nameof(HasReceivedSource))]
    [NotifyPropertyChangedFor(nameof(ReceivedExpirationText))]
    [NotifyPropertyChangedFor(nameof(HasReceivedExpiration))]
    public partial SendReceivedResult? LastReceived { get; set; }

    public string ReceivedSourceText => LastReceived?.CreatorIdentifier is { Length: > 0 } creator ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text4E5D1B1D205B4303,creator) : string.Empty;
    public bool HasReceivedSource => !string.IsNullOrEmpty(ReceivedSourceText);
    public string ReceivedExpirationText => LastReceived?.ExpirationDate is { } expiration
        ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextC2C6F5E95F2D9881,expiration.ToLocalTime()) : string.Empty;
    public bool HasReceivedExpiration => !string.IsNullOrEmpty(ReceivedExpirationText);

    partial void OnLastReceivedChanging(SendReceivedResult? value)
    {
        if (!ReferenceEquals(LastReceived, value))
            LastReceived?.Dispose();
    }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool ShowNotice => !string.IsNullOrEmpty(Notice);
    public bool HasItems => FilteredItems.Count > 0;
    public bool NoItems => !HasItems;
    public bool ShowLoading => LoadState == DataPageState.Loading;
    public bool ShowError => LoadState == DataPageState.Error;
    public bool ShowEmpty => LoadState == DataPageState.Empty;
    public bool ShowContent => LoadState == DataPageState.Content;
    public bool CanCreateSend => !IsReadOnly && !IsBusy;
    public bool ShowStaleInfo => IsReadOnly;
    public bool HasReceivedText => !string.IsNullOrEmpty(ReceivedText);
    public bool ShowReceivedTextContent => HasReceivedText && ReceivedTextRevealed;
    public bool HasHiddenReceivedText => HasReceivedText && !ReceivedTextRevealed;
    public string DisplayedReceivedText => ShowReceivedTextContent ? ReceivedText! : string.Empty;
    public bool HasReceivedFile => !string.IsNullOrEmpty(ReceivedFileName);
    public bool HasCopyFeedback => !string.IsNullOrEmpty(CopyFeedback);
    public bool CanOpenReceivedLink => !IsBusy && !string.IsNullOrWhiteSpace(ReceivedLinkUrl);
    public bool CanReceiveInput => !IsBusy;
    public string ReceiveButtonText => IsBusy ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text821FFB8CD6A23594) : LastReceived?.Ok == true ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9F24062367F05167) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC771248E511FBF93);
    public bool IsAllFilter => SelectedFilterTag == "send:all";
    public bool IsTextFilter => SelectedFilterTag == "send:text";
    public bool IsFileFilter => SelectedFilterTag == "send:file";
    public string AllFilterLabel => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextBDCCA87BC4DDC01F,Items.Count);
    public string TextFilterLabel => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text5CF78DC119C9A278,Items.Count(item => item.Type == SendType.Text));
    public string FileFilterLabel => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextD32741192CB2390F,Items.Count(item => item.Type == SendType.File));

    internal SendViewModel(
        ISendUiService service,
        IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
    }

    public SendViewModel(
        ISendUiService service,
        IClipboardService? clipboard,
        Vault.VaultSession session,
        IAppLifecycleCoordinator lifecycle)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _clipboard = clipboard;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    // latest-wins 四态加载:先取消上一个进行中的加载,只有最新 requestId 能提交集合与 LoadState;
    // 离页(OnNavigatedFrom)或锁屏(ClearSensitive)调用 CancelLoad 使迟到结果被丢弃。加载不占用写 busy(IsBusy)。
    public async Task<UiOperationResult<IReadOnlyList<SendListItem>>> LoadAsync(CancellationToken ct = default)
    {
        if (!TryCaptureExpectedSession(out var expectedSession))
            return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();

        CancelLoad();
        var requestId = Interlocked.Increment(ref _loadRequestId);
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loadCts.Token;
        var previousLoadState = LoadState;
        var previousError = Error;
        if (!TryCommitForExpectedSession(expectedSession, () =>
            {
                if (requestId != Volatile.Read(ref _loadRequestId))
                    return false;
                LoadState = DataPageState.Loading;
                Error = null;
                IsReadOnly = IsExpectedSessionDataStale(expectedSession);
                return true;
            }))
        {
            return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
        }
        try
        {
            var sends = await LoadSnapshotWithOneRetryAsync(token);
            if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (requestId != Volatile.Read(ref _loadRequestId)
                        || token.IsCancellationRequested)
                        return false;
                    Items.Clear();
                    foreach (var send in sends)
                        Items.Add(send);
                    ApplyFilter();
                    UpdateReconciliationNotice();
                    LoadState = FilteredItems.Count == 0 ? DataPageState.Empty : DataPageState.Content;
                    LoadedSession = expectedSession;
                    IsReadOnly = IsExpectedSessionDataStale(expectedSession);
                    return true;
                }))
            {
                return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
            }
            return UiOperationResult<IReadOnlyList<SendListItem>>.Success(sends);
        }
        catch (SendSnapshotSupersededException)
        {
            TryCommitForExpectedSession(expectedSession, () =>
            {
                if (requestId != Volatile.Read(ref _loadRequestId))
                    return false;
                LoadState = previousLoadState;
                Error = previousError;
                return true;
            });
            return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, token))
        {
            return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            var message = UiExceptionPolicy.RequestTimeoutMessage;
            if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (requestId != Volatile.Read(ref _loadRequestId))
                        return false;
                    Error = message;
                    LoadState = DataPageState.Error;
                    return true;
                }))
            {
                return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
            }
            return UiOperationResult<IReadOnlyList<SendListItem>>.Failure(message);
        }
        catch (OrganizationPolicyException ex)
        {
            if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (requestId != Volatile.Read(ref _loadRequestId))
                        return false;
                    Error = ex.Message;
                    LoadState = DataPageState.Error;
                    return true;
                }))
            {
                return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
            }
            return UiOperationResult<IReadOnlyList<SendListItem>>.Failure(ex.Message);
        }
        catch (HttpRequestException)
        {
            var message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text29CFDC6C9F21C924);
            if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (requestId != Volatile.Read(ref _loadRequestId))
                        return false;
                    Error = message;
                    LoadState = DataPageState.Error;
                    return true;
                }))
            {
                return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
            }
            return UiOperationResult<IReadOnlyList<SendListItem>>.Failure(message);
        }
    }

    // 取消当前加载:递增 requestId 使进行中的旧请求结果被丢弃,并取消其令牌。离页/锁屏时调用。
    public void CancelLoad()
    {
        Interlocked.Increment(ref _loadRequestId);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    public void SelectFilterByTag(string? tag)
    {
        SelectedFilterTag = tag is "send:text" or "send:file" or "send:all" ? tag : "send:all";
    }

    // 编辑打开路径:取完整 Send 水合 draft(F1)。找不到返回 null。
    public async Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default)
    {
        if (!TryCaptureExpectedSession(out var expectedSession))
            return null;

        SendEditorDraft? draft;
        try
        {
            draft = await _service.GetEditDraftAsync(id, ct);
        }
        catch (SendSnapshotSupersededException)
        {
            return null;
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return null;
        }
        if (draft is null)
            return null;

        if (!TryCommitForExpectedSession(expectedSession, () =>
            {
                draft.OriginatingSession = expectedSession;
                return true;
            }))
        {
            draft.ClearSensitive();
            return null;
        }
        return draft;
    }

    private bool TryCaptureExpectedSession(out VaultSessionLease? expectedSession)
    {
        if (_session is null)
        {
            expectedSession = null;
            return true;
        }

        try
        {
            var captured = _session.CaptureLease();
            if (!_session.IsUnlocked(captured))
            {
                expectedSession = null;
                return false;
            }
            expectedSession = captured;
            return true;
        }
        catch (InvalidOperationException)
        {
            expectedSession = null;
            return false;
        }
    }

    private bool TryCommitForExpectedSession(
        VaultSessionLease? expectedSession,
        Func<bool> commit)
    {
        if (_session is null)
            return commit();
        if (expectedSession is null)
            return false;

        var committed = false;
        var accepted = _lifecycle!.TryRunSensitive(
            () => _session.TryRunIfUnlocked(
                expectedSession,
                () => committed = commit()));
        return accepted && committed;
    }

    private bool IsExpectedSessionDataStale(VaultSessionLease? expectedSession)
    {
        if (_session is null || expectedSession is null)
            return false;
        return _session.IsDataStale(expectedSession);
    }

    partial void OnSelectedFilterTagChanged(string value)
    {
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsTextFilter));
        OnPropertyChanged(nameof(IsFileFilter));
        ApplyFilter();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();
    partial void OnSortByDeletionChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        IEnumerable<SendListItem> source = SelectedFilterTag switch
        {
            "send:text" => Items.Where(i => i.Type == SendType.Text),
            "send:file" => Items.Where(i => i.Type == SendType.File),
            _ => Items,
        };
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var query = SearchQuery.Trim();
            source = source.Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.TextContent?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                || item.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
        }
        source = SortByDeletion
            ? source.OrderBy(item => item.DeletionDateTime ?? DateTimeOffset.MaxValue)
            : source.OrderByDescending(item => item.Id == LastCommittedItem?.Id)
                .ThenByDescending(item => item.RevisionDate ?? DateTimeOffset.MinValue);
        foreach (var item in source) FilteredItems.Add(item);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(NoItems));
        OnPropertyChanged(nameof(AllFilterLabel));
        OnPropertyChanged(nameof(TextFilterLabel));
        OnPropertyChanged(nameof(FileFilterLabel));
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(ListCountLabel));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(ShowSearchEmptyActions));
        OnPropertyChanged(nameof(ShowSearchAllEmptyAction));
        OnPropertyChanged(nameof(ShowCreateEmptyAction));
        if (LoadState is DataPageState.Empty or DataPageState.Content)
            LoadState = FilteredItems.Count == 0 ? DataPageState.Empty : DataPageState.Content;
    }

    public void RefreshAvailability(DateTimeOffset now)
    {
        for (var index = 0; index < Items.Count; index++)
            Items[index] = Items[index] with { DisplayTime = now };
        for (var index = 0; index < FilteredItems.Count; index++)
            FilteredItems[index] = FilteredItems[index] with { DisplayTime = now };
    }

    // 创建 Send:写闸门去重,返回明确四态结果。失败(已知异常)保留草稿与 draft.ReplacementFile 供重试,
    // 只映射为安全错误文案、不泄露底层异常细节;未知异常不 catch,交由上层/Task 12 统一处理。
    public async Task<UiOperationResult> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
    {
        var expectedSession = draft.OriginatingSession;
        if (!TryCommitForExpectedSession(expectedSession, static () => true))
            return UiOperationResult.Cancelled();
        if (!draft.HasRequiredData())
            return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EA7D2C9D46CC3A5));
        if (!_writeGate.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            var stateEpoch = Volatile.Read(ref _stateEpoch);
            if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch))
                        return false;
                    IsBusy = true;
                    Error = null;
                    Notice = null;
                    return true;
                }))
            {
                return UiOperationResult.Cancelled();
            }
            try
            {
                var item = await _service.CreateSendAsync(draft, file, ct);
                if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch) || ct.IsCancellationRequested)
                        return false;
                    UpsertCommittedItem(item, replacedId: null);
                    UpdateReconciliationNotice();
                    return true;
                }))
                {
                    return UiOperationResult.Cancelled();
                }
                return UiOperationResult.Success();
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                return UiOperationResult.Cancelled();
            }
            catch (Exception ex) when (
                ex is ArgumentOutOfRangeException
                    or Vault.SendStateStaleException
                    or OrganizationPolicyException
                    or UnauthorizedAccessException
                    or IOException
                    or InvalidDataException
                    or Vault.SendUploadCompensationException
                    or HttpRequestException
                || RequestTimeoutClassifier.IsTimeout(ex, ct))
            {
                var error = SafeSendError(ex);
                if (!TryCommitForExpectedSession(expectedSession, () =>
                    {
                        if (stateEpoch != Volatile.Read(ref _stateEpoch))
                            return false;
                        Error = error;
                        return true;
                    }))
                {
                    return UiOperationResult.Cancelled();
                }
                return UiOperationResult.Failure(error);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    // 更新 Send:写闸门去重,返回明确四态结果。失败不改集合、不清 draft.ReplacementFile,允许原样重试。
    public async Task<UiOperationResult> UpdateSendFromDraftAsync(
        SendListItem item,
        SendEditorDraft draft,
        SendFileSource? file,
        CancellationToken ct = default)
    {
        var expectedSession = draft.OriginatingSession;
        if (!TryCommitForExpectedSession(expectedSession, static () => true))
            return UiOperationResult.Cancelled();
        if (!draft.HasRequiredData())
            return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EA7D2C9D46CC3A5));
        if (Items.All(existing => existing.Id != item.Id))
            return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF0F5474965782B06));
        if (!item.CanModify)
            return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63E3CCCCD3D26E16));
        if (!_writeGate.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            var stateEpoch = Volatile.Read(ref _stateEpoch);
            if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch))
                        return false;
                    IsBusy = true;
                    Error = null;
                    Notice = null;
                    return true;
                }))
            {
                return UiOperationResult.Cancelled();
            }
            try
            {
                var updated = await _service.UpdateSendAsync(item.Id, draft, file, ct);
                if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch) || ct.IsCancellationRequested)
                        return false;
                    UpsertCommittedItem(updated, item.Id);
                    UpdateReconciliationNotice();
                    return true;
                }))
                {
                    return UiOperationResult.Cancelled();
                }
                return UiOperationResult.Success();
            }
            catch (Vault.SendReplacementUncertainException uncertain)
            {
                if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch) || ct.IsCancellationRequested)
                        return false;
                    var replacement = new SendListItem(
                        uncertain.Replacement.SendId,
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text641CF762D56CA5C1),
                        item.Type,
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7EE0F1015D52DB6B),
                        null,
                        SendReconciliationState.ReplacementNewPending);
                    MarkReplacementPending(uncertain.ExistingSendId, replacement);
                    UpdateReconciliationNotice();
                    return true;
                }))
                {
                    return UiOperationResult.Cancelled();
                }
                return UiOperationResult.Success();
            }
            catch (SendMutationInProgressException)
            {
                return UiOperationResult.Busy();
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                return UiOperationResult.Cancelled();
            }
            catch (Exception ex) when (
                ex is ArgumentOutOfRangeException
                    or Vault.SendStateStaleException
                    or OrganizationPolicyException
                    or UnauthorizedAccessException
                    or IOException
                    or InvalidDataException
                    or Vault.SendUploadCompensationException
                    or Vault.SendPasswordRequiredForReplacementException
                    or Vault.SendUnsupportedAuthenticationException
                    or HttpRequestException
                || RequestTimeoutClassifier.IsTimeout(ex, ct))
            {
                var error = SafeSendError(ex);
                if (!TryCommitForExpectedSession(expectedSession, () =>
                    {
                        if (stateEpoch != Volatile.Read(ref _stateEpoch))
                            return false;
                        Error = error;
                        return true;
                    }))
                {
                    return UiOperationResult.Cancelled();
                }
                return UiOperationResult.Failure(error);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    // Maps known write failures to stable, actionable messages without exposing diagnostics.
    private static string SafeSendError(Exception exception)
    {
        if (exception is Vault.SendUploadCompensationException)
        {
            return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB46954B063E1EF25);
        }
        if (RequestTimeoutClassifier.IsTimeout(exception))
            return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2019E7CD5801502A);

        return exception switch
        {
            ArgumentOutOfRangeException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFC35EAAAAD04F005),
            Vault.SendStateStaleException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text58474BA7DECC46E5),
            OrganizationPolicyException => exception.Message,
            UnauthorizedAccessException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA2DAD4AAC80F11EE),
            IOException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCA8D995DCC461C2C),
            InvalidDataException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE703C225884E501D),
            Vault.SendPasswordRequiredForReplacementException =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0DCAC88E947C603A),
            Vault.SendUnsupportedAuthenticationException =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE982AD674F1812B1),
            HttpRequestException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBD573FBAE00C37F5),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3D272C0EDE2F2F5A),
        };
    }

    private static bool IsRecoverableDeleteError(Exception exception) =>
        RequestTimeoutClassifier.IsTimeout(exception)
        || exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or Vault.SendStateStaleException
            or OrganizationPolicyException
            or SendMutationInProgressException;

    private static string SafeDeleteError(Exception exception)
    {
        if (RequestTimeoutClassifier.IsTimeout(exception)
            || exception is HttpRequestException or IOException)
        {
            return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7445C1ACFE1DA340);
        }

        return exception switch
        {
            UnauthorizedAccessException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8E3331AEDD14EFA1),
            Vault.SendStateStaleException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE3195AD3EDB1487E),
            OrganizationPolicyException => exception.Message,
            SendMutationInProgressException => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text24E4F0237595FF9E),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text97332CE635B030AE),
        };
    }

    [RelayCommand]
    private async Task DeleteSendAsync(SendDeleteRequest? request)
    {
        var item = request?.Item;
        if (item is null)
            return;
        var expectedSession = request!.OriginatingSession;
        if (!TryCommitForExpectedSession(expectedSession, static () => true))
            return;
        if (!item.CanModify)
        {
            TryCommitForExpectedSession(expectedSession, () =>
            {
                Notice = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC72CB7230007DF42);
                return true;
            });
            return;
        }
        if (!_writeGate.TryEnter(out var operation))
            return;

        using (operation)
        {
            var stateEpoch = Volatile.Read(ref _stateEpoch);
            if (!TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch))
                        return false;
                    IsBusy = true;
                    Error = null;
                    return true;
                }))
            {
                return;
            }
            try
            {
                await _service.DeleteSendAsync(item.Id, expectedSession);
                TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch))
                        return false;
                    var existing = Items.FirstOrDefault(send => send.Id == item.Id);
                    if (existing is not null)
                        Items.Remove(existing);
                    ApplyFilter();
                    return true;
                });
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex))
            {
            }
            catch (Exception ex) when (IsRecoverableDeleteError(ex))
            {
                TryCommitForExpectedSession(expectedSession, () =>
                {
                    if (stateEpoch != Volatile.Read(ref _stateEpoch))
                        return false;
                    Error = SafeDeleteError(ex);
                    return true;
                });
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    private async Task<IReadOnlyList<SendListItem>> LoadSnapshotWithOneRetryAsync(
        CancellationToken ct)
    {
        try
        {
            return await _service.GetSendsAsync(ct);
        }
        catch (SendSnapshotSupersededException)
        {
            await Task.Yield();
            return await _service.GetSendsAsync(ct);
        }
    }

    public bool CanModifySend(SendListItem? item) => !IsReadOnly && item?.CanModify == true;

    private void UpsertCommittedItem(SendListItem committed, string? replacedId)
    {
        LastCommittedItem = committed;
        if (!string.IsNullOrEmpty(replacedId)
            && replacedId != committed.Id
            && committed.ReconciliationState != SendReconciliationState.ReplacementNewPending)
        {
            var replaced = Items.FirstOrDefault(item => item.Id == replacedId);
            if (replaced is not null)
                Items.Remove(replaced);
        }

        var existing = Items.FirstOrDefault(item => item.Id == committed.Id);
        if (existing is null)
            Items.Add(committed);
        else
            Items[Items.IndexOf(existing)] = committed;

        if (committed.ReconciliationState == SendReconciliationState.ReplacementNewPending
            && !string.IsNullOrEmpty(replacedId))
        {
            MarkReplacementPending(replacedId, committed);
            return;
        }

        ApplyFilter();
    }

    private void MarkReplacementPending(string existingSendId, SendListItem replacement)
    {
        LastCommittedItem = replacement;
        var existing = Items.FirstOrDefault(item => item.Id == existingSendId);
        if (existing is not null)
        {
            Items[Items.IndexOf(existing)] = existing with
            {
                ReconciliationState = SendReconciliationState.ReplacementOriginalPending,
            };
        }

        var currentReplacement = Items.FirstOrDefault(item => item.Id == replacement.Id);
        if (currentReplacement is null)
            Items.Add(replacement);
        else
            Items[Items.IndexOf(currentReplacement)] = replacement;
        ApplyFilter();
    }

    private void UpdateReconciliationNotice()
    {
        if (Items.Any(item => item.ReconciliationState is
                SendReconciliationState.ReplacementOriginalPending or
                SendReconciliationState.ReplacementNewPending))
        {
            Notice = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text76FF57910C807607);
            return;
        }

        Notice = Items.Any(item => item.ReconciliationState == SendReconciliationState.CommittedPendingRefresh)
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9C84F79C173481F6)
            : null;
    }

    public void MarkMoreMenuOpened(SendListItem? item) => SelectedMenuItem = item;

    [RelayCommand]
    private void CopyLink(SendListItem? item)
    {
        if (item is null)
            return;
        TryCommitForExpectedSession(LoadedSession, () =>
        {
            CopyFeedback = null;
            try
            {
                var link = _service.CopyShareLink(item);
                if (string.IsNullOrWhiteSpace(link) || _clipboard is null)
                {
                    Error = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text97CA9BAE0FF9E0AF);
                    return true;
                }

                _clipboard.SetSecretText(link);
                (_clipboard as IClipboardStatus)?.SetLabel(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text305378DDD2B86B60));
                Error = null;
                var availability = item.GetAvailability(DateTimeOffset.UtcNow);
                CopyFeedback = availability.State switch
                {
                    SendAvailabilityState.Available => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0A06CC0DBF502809),
                    SendAvailabilityState.Unknown or SendAvailabilityState.AwaitingConfirmation =>
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text157248F5EF6A1DCF),
                    _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF50AA7E15F08AFD7),
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                Error = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text236CA12167D7B860);
            }
            return true;
        });
    }

    [RelayCommand(CanExecute = nameof(CanOpenReceivedLink))]
    private async Task OpenReceivedLinkAsync(CancellationToken ct = default)
    {
        var (requestId, operationCts) = BeginReceiveOperation(ct);
        ReceivedText = null;
        ReceivedTextRevealed = false;
        ReceivedFileName = null;
        ReceivedWrongPassword = false;
        LastReceived = null;
        Error = null;

        if (string.IsNullOrWhiteSpace(ReceivedLinkUrl))
        {
            CompleteReceiveOperation(requestId, operationCts);
            return;
        }

        IsBusy = true;
        try
        {
            var password = string.IsNullOrEmpty(ReceivedLinkPassword) ? null : ReceivedLinkPassword;
            var result = await _service.OpenReceivedLinkAsync(
                ReceivedLinkUrl.Trim(),
                password,
                operationCts.Token);
            if (!TryCommitReceiveOperation(requestId, operationCts, () => ApplyReceivedResult(result)))
            {
                result.Dispose();
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, operationCts.Token))
        {
        }
        catch (Exception ex) when (SendUiErrorMessages.IsRecoverableAccessFailure(ex))
        {
            var message = SendUiErrorMessages.ForAccessFailure(ex);
            TryCommitReceiveOperation(requestId, operationCts, () => Error = message);
        }
        finally
        {
            CompleteReceiveOperation(requestId, operationCts);
        }
    }

    private void ApplyReceivedResult(SendReceivedResult result)
    {
        LastReceived = result;
        if (result.WrongPassword)
        {
            ReceivedWrongPassword = true;
            return;
        }
        if (!result.Ok)
        {
            Error = SendUiErrorMessages.SanitizeAccessResultError(result.Error);
            return;
        }
        ReceivedLinkPassword = string.Empty;
        ReceivedLinkUrl = string.Empty;
        if (result.Type == SendType.File)
            ReceivedFileName = result.FileName;
        else
        {
            ReceivedTextRevealed = !result.TextHidden;
            ReceivedText = result.TextContent;
        }
    }

    [RelayCommand]
    private void RevealReceivedText()
    {
        if (HasReceivedText) ReceivedTextRevealed = true;
    }

    private (long RequestId, CancellationTokenSource Cts) BeginReceiveOperation(CancellationToken ct)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        long requestId;
        lock (_receiveGate)
        {
            previous = _receiveCts;
            current = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _receiveCts = current;
            requestId = ++_receiveRequestId;
        }

        SafeCancelAndDispose(previous);
        return (requestId, current);
    }

    private bool TryCommitReceiveOperation(
        long requestId,
        CancellationTokenSource operationCts,
        Action commit)
    {
        lock (_receiveGate)
        {
            if (requestId != _receiveRequestId
                || !ReferenceEquals(_receiveCts, operationCts)
                || operationCts.IsCancellationRequested)
                return false;

            commit();
            return true;
        }
    }

    private void CompleteReceiveOperation(long requestId, CancellationTokenSource operationCts)
    {
        lock (_receiveGate)
        {
            if (requestId == _receiveRequestId && ReferenceEquals(_receiveCts, operationCts))
            {
                _receiveCts = null;
                IsBusy = false;
            }
        }

        SafeDispose(operationCts);
    }

    public void CancelReceive()
    {
        CancellationTokenSource? operation;
        lock (_receiveGate)
        {
            _receiveRequestId++;
            operation = _receiveCts;
            _receiveCts = null;
            IsBusy = false;
        }

        SafeCancelAndDispose(operation);
    }

    private static void SafeCancelAndDispose(CancellationTokenSource? source)
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
            // Cancellation callbacks cannot preserve a superseded receive operation.
        }
        finally
        {
            SafeDispose(source);
        }
    }

    private static void SafeDispose(CancellationTokenSource source)
    {
        try
        {
            source.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    [RelayCommand]
    private void Add()
    {
    }

    [RelayCommand]
    private void More(SendListItem? item) => MarkMoreMenuOpened(item);

    // 锁屏/登出时清空内存中的机密足迹:取消进行中的加载、丢弃收到的 Send 链接/密码/明文/文件名与列表项。
    // 经 DI TrackSensitive 弱登记后由生命周期协调器统一调用。
    public void ClearSensitive()
    {
        Interlocked.Increment(ref _stateEpoch);
        CancelLoad();
        CancelReceive();
        SelectedMenuItem = null;
        ReceivedLinkUrl = string.Empty;
        ReceivedLinkPassword = string.Empty;
        ReceivedText = null;
        ReceivedTextRevealed = false;
        ReceivedFileName = null;
        ReceivedWrongPassword = false;
        LastReceived = null;
        Error = null;
        Notice = null;
        CopyFeedback = null;
        Items.Clear();
        FilteredItems.Clear();
        LoadedSession = null;
        LastCommittedItem = null;
        SearchQuery = string.Empty;
        IsReadOnly = false;
        LoadState = DataPageState.Loading;
    }
}
