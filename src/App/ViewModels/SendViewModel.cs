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
        "send:text" => "文本 Send",
        "send:file" => "文件 Send",
        _ => "全部 Send",
    };
    public string ListCountLabel => $"{FilteredItems.Count}";
    public string EmptyTitle => !string.IsNullOrWhiteSpace(SearchQuery)
        ? $"没有找到“{SearchQuery}”"
        : SelectedFilterTag switch
        {
            "send:text" => "还没有文本 Send",
            "send:file" => "还没有文件 Send",
            _ => "还没有 Send",
        };

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
    [NotifyPropertyChangedFor(nameof(ReceivedSourceText))]
    [NotifyPropertyChangedFor(nameof(HasReceivedSource))]
    [NotifyPropertyChangedFor(nameof(ReceivedExpirationText))]
    [NotifyPropertyChangedFor(nameof(HasReceivedExpiration))]
    public partial SendReceivedResult? LastReceived { get; set; }

    public string ReceivedSourceText => LastReceived?.Accessed?.CreatorIdentifier is { Length: > 0 } creator ? $"来自 {creator}" : string.Empty;
    public bool HasReceivedSource => !string.IsNullOrEmpty(ReceivedSourceText);
    public string ReceivedExpirationText => LastReceived?.Accessed?.ExpirationDate is { } expiration
        ? $"{expiration.ToLocalTime():M 月 d 日 HH:mm} 过期" : string.Empty;
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
    public string ReceiveButtonText => IsBusy ? "正在打开…" : "打开";
    public bool IsAllFilter => SelectedFilterTag == "send:all";
    public bool IsTextFilter => SelectedFilterTag == "send:text";
    public bool IsFileFilter => SelectedFilterTag == "send:file";
    public string AllFilterLabel => $"全部 · {Items.Count}";
    public string TextFilterLabel => $"文本 · {Items.Count(item => item.Type == SendType.Text)}";
    public string FileFilterLabel => $"文件 · {Items.Count(item => item.Type == SendType.File)}";

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
            const string message = "无法加载 Send，请检查网络后重试。";
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
            return UiOperationResult.Failure("请填写 Send 必填项。");
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
            return UiOperationResult.Failure("请填写 Send 必填项。");
        if (Items.All(existing => existing.Id != item.Id))
            return UiOperationResult.Failure("要更新的 Send 已不存在。");
        if (!item.CanModify)
            return UiOperationResult.Failure("该 Send 正在等待服务器对账，暂时不能修改。");
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
                        "已提交的 Send",
                        item.Type,
                        "待服务器确认",
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
            return "文件上传未完成，服务器可能保留了未完成的 Send。请刷新列表，确认并删除该条目后再重试。";
        }
        if (RequestTimeoutClassifier.IsTimeout(exception))
            return "文件传输长时间没有进展，请检查连接后重试。";

        return exception switch
        {
            ArgumentOutOfRangeException => "文件超过 500 MiB 上限。",
            Vault.SendStateStaleException => "离线数据为只读。请先成功同步密码库，再修改 Send。",
            OrganizationPolicyException => exception.Message,
            UnauthorizedAccessException => "没有读取所选文件的权限。",
            IOException => "读取或上传文件失败，请确认文件仍可访问后重试。",
            InvalidDataException => "文件在读取或上传过程中已被修改，请重新选择文件后重试。",
            Vault.SendPasswordRequiredForReplacementException =>
                "此 Send 已设置密码。替换文件时请重新输入原密码或设置新密码。",
            Vault.SendUnsupportedAuthenticationException =>
                "此 Send 使用当前版本尚不支持的验证方式，暂时不能修改。",
            HttpRequestException => "网络请求失败，请检查连接后重试。",
            _ => "保存 Send 失败。",
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
            return "无法确认删除结果，请刷新列表检查该 Send 是否仍存在后再操作。";
        }

        return exception switch
        {
            UnauthorizedAccessException => "没有删除此 Send 的权限，请重新登录或联系管理员后重试。",
            Vault.SendStateStaleException => "离线数据为只读。请先成功同步密码库，再删除 Send。",
            OrganizationPolicyException => exception.Message,
            SendMutationInProgressException => "另一项 Send 操作仍在进行，请稍后重试。",
            _ => "删除 Send 失败，请刷新列表后重试。",
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
                Notice = "该 Send 正在等待服务器对账，暂时不能编辑或删除。";
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
            Notice = "文件替换已上传，但旧 Send 的删除结果尚未确认。相关条目已锁定，请刷新并等待服务器完成对账。";
            return;
        }

        Notice = Items.Any(item => item.ReconciliationState == SendReconciliationState.CommittedPendingRefresh)
            ? "更改已保存到服务器，但列表尚未刷新。该条目已锁定，请稍后重新加载。"
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
                    Error = "链接尚不可用，请刷新列表后重试。";
                    return true;
                }

                _clipboard.SetSecretText(link);
                (_clipboard as IClipboardStatus)?.SetLabel("链接");
                Error = null;
                var availability = item.GetAvailability(DateTimeOffset.UtcNow);
                CopyFeedback = availability.State switch
                {
                    SendAvailabilityState.Available => "链接已复制。请仅与预期接收者分享。",
                    SendAvailabilityState.Unknown or SendAvailabilityState.AwaitingConfirmation =>
                        "链接已复制。请同步确认接收者的访问状态。",
                    _ => "链接已复制。此 Send 当前不可访问，请检查停用状态、访问期限和访问次数。",
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                Error = "无法复制链接，请稍后重试。";
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
            ReceivedTextRevealed = result.Accessed?.TextHidden != true;
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
