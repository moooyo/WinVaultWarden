using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;

namespace App.ViewModels;

public partial class SendViewModel : ObservableObject, ISensitiveState
{
    private readonly ISendUiService _service;
    private readonly IClipboardService? _clipboard;
    // 写操作(创建/更新)单持有者闸门:避免重入并发写。加载状态(LoadState)独立于写 busy(IsBusy)。
    private readonly AsyncOperationGate _writeGate = new();
    // latest-wins 加载:每次 LoadAsync 递增 requestId 并取消上一个 CTS;只有最新请求能提交集合与四态。
    private CancellationTokenSource? _loadCts;
    private long _loadRequestId;

    public ObservableCollection<SendListItem> Items { get; } = new();
    public ObservableCollection<SendListItem> FilteredItems { get; } = new();

    [ObservableProperty]
    public partial string SelectedFilterTag { get; set; } = "send:all";

    [ObservableProperty]
    public partial SendListItem? SelectedMenuItem { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

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
    public partial string ReceivedLinkUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReceivedLinkPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceivedText))]
    public partial string? ReceivedText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceivedFile))]
    public partial string? ReceivedFileName { get; set; }

    [ObservableProperty]
    public partial bool ReceivedWrongPassword { get; set; }

    [ObservableProperty]
    public partial SendReceivedResult? LastReceived { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasItems => FilteredItems.Count > 0;
    public bool NoItems => !HasItems;
    public bool ShowLoading => LoadState == DataPageState.Loading;
    public bool ShowError => LoadState == DataPageState.Error;
    public bool ShowEmpty => LoadState == DataPageState.Empty;
    public bool ShowContent => LoadState == DataPageState.Content;
    public bool HasReceivedText => !string.IsNullOrEmpty(ReceivedText);
    public bool HasReceivedFile => !string.IsNullOrEmpty(ReceivedFileName);

    public SendViewModel(ISendUiService service, IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
    }

    // latest-wins 四态加载:先取消上一个进行中的加载,只有最新 requestId 能提交集合与 LoadState;
    // 离页(OnNavigatedFrom)或锁屏(ClearSensitive)调用 CancelLoad 使迟到结果被丢弃。加载不占用写 busy(IsBusy)。
    public async Task<UiOperationResult<IReadOnlyList<SendListItem>>> LoadAsync(CancellationToken ct = default)
    {
        CancelLoad();
        var requestId = Interlocked.Increment(ref _loadRequestId);
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _loadCts.Token;
        LoadState = DataPageState.Loading;
        Error = null;
        try
        {
            var sends = await _service.GetSendsAsync(token);
            if (requestId != Volatile.Read(ref _loadRequestId))
                return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
            Items.Clear();
            foreach (var send in sends)
                Items.Add(send);
            ApplyFilter();
            LoadState = Items.Count == 0 ? DataPageState.Empty : DataPageState.Content;
            return UiOperationResult<IReadOnlyList<SendListItem>>.Success(sends);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
        }
        catch (HttpRequestException)
        {
            // 旧 load 的失败不得覆盖新 load 的状态。
            if (requestId != Volatile.Read(ref _loadRequestId))
                return UiOperationResult<IReadOnlyList<SendListItem>>.Cancelled();
            Error = "无法加载 Send，请检查网络后重试。";
            LoadState = DataPageState.Error;
            return UiOperationResult<IReadOnlyList<SendListItem>>.Failure(Error);
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
    public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default) =>
        _service.GetEditDraftAsync(id, ct);

    partial void OnSelectedFilterTagChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        IEnumerable<SendListItem> source = SelectedFilterTag switch
        {
            "send:text" => Items.Where(i => i.Type == SendType.Text),
            "send:file" => Items.Where(i => i.Type == SendType.File),
            _ => Items,
        };
        foreach (var item in source) FilteredItems.Add(item);
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(NoItems));
    }

    // 创建 Send:写闸门去重,返回明确四态结果。失败(已知异常)保留草稿与 draft.ReplacementFile 供重试,
    // 只映射为安全错误文案、不泄露底层异常细节;未知异常不 catch,交由上层/Task 12 统一处理。
    public async Task<UiOperationResult> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
    {
        if (!draft.HasRequiredData())
            return UiOperationResult.Failure("请填写 Send 必填项。");
        if (!_writeGate.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            IsBusy = true;
            Error = null;
            try
            {
                var item = await _service.CreateSendAsync(draft, file, ct);
                Items.Add(item);
                ApplyFilter();
                return UiOperationResult.Success();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return UiOperationResult.Cancelled();
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException
                or UnauthorizedAccessException
                or IOException
                or InvalidDataException
                or Vault.SendUploadCompensationException
                or HttpRequestException)
            {
                Error = SafeSendError(ex);
                return UiOperationResult.Failure(Error);
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
        if (!draft.HasRequiredData())
            return UiOperationResult.Failure("请填写 Send 必填项。");
        if (!_writeGate.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            IsBusy = true;
            Error = null;
            try
            {
                var updated = await _service.UpdateSendAsync(item.Id, draft, file, ct);
                var existing = Items.FirstOrDefault(value => value.Id == item.Id);
                if (existing is null)
                    return UiOperationResult.Failure("要更新的 Send 已不存在。");
                Items[Items.IndexOf(existing)] = updated;
                ApplyFilter();
                return UiOperationResult.Success();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return UiOperationResult.Cancelled();
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException
                or UnauthorizedAccessException
                or IOException
                or InvalidDataException
                or Vault.SendUploadCompensationException
                or HttpRequestException)
            {
                Error = SafeSendError(ex);
                return UiOperationResult.Failure(Error);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    // 安全错误映射:把已知写入异常翻译成用户可读、不泄露细节的中文文案。
    private static string SafeSendError(Exception exception) => exception switch
    {
        ArgumentOutOfRangeException => "文件超过 500 MiB 上限。",
        UnauthorizedAccessException => "没有读取所选文件的权限。",
        IOException => "读取或上传文件失败，请确认文件仍可访问后重试。",
        InvalidDataException => "文件在读取或上传过程中已被修改，请重新选择文件后重试。",
        Vault.SendUploadCompensationException compensation => compensation.Message,
        HttpRequestException => "网络请求失败，请检查连接后重试。",
        _ => "保存 Send 失败。",
    };

    [RelayCommand]
    private async Task DeleteSendAsync(SendListItem? item)
    {
        if (item is null)
            return;
        IsBusy = true;
        Error = null;
        try
        {
            await _service.DeleteSendAsync(item.Id);
            var existing = Items.FirstOrDefault(s => s.Id == item.Id);
            if (existing is not null) Items.Remove(existing);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void MarkMoreMenuOpened(SendListItem? item) => SelectedMenuItem = item;

    [RelayCommand]
    private void CopyLink(SendListItem? item)
    {
        if (item is null)
            return;
        var link = _service.CopyShareLink(item);
        if (!string.IsNullOrWhiteSpace(link))
            _clipboard?.SetSecretText(link);
    }

    [RelayCommand]
    private async Task OpenReceivedLinkAsync(CancellationToken ct = default)
    {
        ReceivedText = null;
        ReceivedFileName = null;
        ReceivedWrongPassword = false;
        LastReceived = null;
        Error = null;

        if (string.IsNullOrWhiteSpace(ReceivedLinkUrl))
            return;

        IsBusy = true;
        try
        {
            var password = string.IsNullOrEmpty(ReceivedLinkPassword) ? null : ReceivedLinkPassword;
            var result = await _service.OpenReceivedLinkAsync(ReceivedLinkUrl.Trim(), password, ct);
            LastReceived = result;
            if (result.WrongPassword)
            {
                ReceivedWrongPassword = true;
                return;
            }
            if (!result.Ok)
            {
                Error = result.Error ?? "无法打开该 Send 链接。";
                return;
            }
            if (result.Type == SendType.File)
                ReceivedFileName = result.FileName;
            else
                ReceivedText = result.TextContent;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
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
        CancelLoad();
        SelectedMenuItem = null;
        ReceivedLinkUrl = string.Empty;
        ReceivedLinkPassword = string.Empty;
        ReceivedText = null;
        ReceivedFileName = null;
        ReceivedWrongPassword = false;
        LastReceived = null;
        Error = null;
        Items.Clear();
        FilteredItems.Clear();
        LoadState = DataPageState.Loading;
    }
}
