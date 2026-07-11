using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

// 页面级 Vault 写入/同步/附件操作的唯一门禁分类:同一时刻只有一种操作可持有 _operationGate,
// 其余冲突操作一律返回 Busy(见 RunOperationAsync)。BusyMessage 依此渲染页面级 live region 文案。
public enum VaultOperationKind
{
    Sync,
    SaveCipher,
    DeleteCipher,
    Folder,
    Batch,
    AttachmentUpload,
    AttachmentDownload,
    AttachmentDelete,
}

public partial class VaultViewModel : ObservableObject, ISensitiveState
{
    private readonly IVaultUiService _service;
    private readonly IClipboardService? _clipboard;
    private readonly IAttachmentUiService? _attachments;
    private readonly ISavedSearchStore? _savedStore;
    private readonly IItemRepromptGate? _reprompt;
    private string? _editingId;
    private bool _suppressApply;
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private readonly AsyncOperationGate _operationGate = new();
    private CancellationTokenSource? _operationCts;

    public ObservableCollection<SavedSearchView> SavedViews { get; } = new();

    public ObservableCollection<CipherListItem> Items { get; } = new();
    public ObservableCollection<FilterNode> Filters { get; } = new();
    public ObservableCollection<CipherListItem> FilteredItems { get; } = new();
    public ObservableCollection<VaultListGroup> GroupedItems { get; } = new();
    public IEnumerable<FilterNode> FolderFilters => Filters.Where(f => f.Kind == FilterKind.Folder);

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; }

    public int SelectedCount => _selectedIds.Count;
    public IReadOnlyCollection<string> SelectedIds => _selectedIds;
    public bool HasSelection => IsSelectionMode && _selectedIds.Count > 0;

    [ObservableProperty]
    public partial CipherListItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSelectionPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowSensitiveUnlock))]
    public partial CipherDetail? Detail { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetTotp { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetAttachment { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetUri { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveFacetCount))]
    [NotifyPropertyChangedFor(nameof(HasActiveFacets))]
    public partial bool FacetFavoriteOnly { get; set; }
    [ObservableProperty]
    public partial VaultSortKey SelectedSort { get; set; } = VaultSortKey.NameAsc;

    public VaultFacets CurrentFacets => new(FacetTotp, FacetAttachment, FacetUri, FacetFavoriteOnly);
    public bool HasActiveRefinement => CurrentFacets.Any || !string.IsNullOrWhiteSpace(SearchText);

    // 分面筛选徽标:激活的分面个数(0–4)与是否有激活分面,供“筛选 ▾”按钮的 InfoBadge 显示。
    public int ActiveFacetCount =>
        (FacetTotp ? 1 : 0) + (FacetAttachment ? 1 : 0) + (FacetUri ? 1 : 0) + (FacetFavoriteOnly ? 1 : 0);
    public bool HasActiveFacets => CurrentFacets.Any;

    /// <summary>供排序 ComboBox.SelectedIndex 双向绑定使用（顺序与 VaultSortKey 枚举一致）。</summary>
    public int SelectedSortIndex
    {
        get => (int)SelectedSort;
        set { if ((int)SelectedSort != value) SelectedSort = (VaultSortKey)value; }
    }

    partial void OnFacetTotpChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnFacetAttachmentChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnFacetUriChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnFacetFavoriteOnlyChanged(bool value) { if (!_suppressApply) ApplyFilter(); }
    partial void OnSelectedSortChanged(VaultSortKey value)
    {
        OnPropertyChanged(nameof(SelectedSortIndex));
        if (!_suppressApply) ApplyFilter();
    }

    [ObservableProperty]
    public partial FilterNode? SelectedFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoSelectionPlaceholder))]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorSelectedFolder))]
    public partial CipherEditorDraft? EditorDraft { get; set; }

    // 文件夹选择:以 FilterNode 形式暴露给 ComboBox 的 SelectedItem,内部映射到 EditorDraft.FolderId。
    // 用 x:Bind + SelectedItem 取代反射式 DisplayMemberPath/SelectedValuePath(AOT 下反射会抛
    // NotSupportedException)。getter 返回的是 FolderFilters 中的同一实例,故 SelectedItem 能按引用命中。
    public FilterNode? EditorSelectedFolder
    {
        get => EditorDraft is null
            ? null
            : FolderFilters.FirstOrDefault(f => f.FolderId == EditorDraft.FolderId);
        set
        {
            if (EditorDraft is not null)
                EditorDraft.FolderId = value?.FolderId;
        }
    }

    [ObservableProperty]
    public partial string EditorError { get; set; } = string.Empty;

    // 页面级唯一 Vault 操作状态:非 null 表示当前有写入/同步/附件操作占用 _operationGate。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(BusyMessage))]
    [NotifyPropertyChangedFor(nameof(CanStartVaultOperation))]
    public partial VaultOperationKind? CurrentOperation { get; set; }

    public bool IsBusy => CurrentOperation is not null;
    public bool CanStartVaultOperation => CurrentOperation is null;
    public string BusyMessage => CurrentOperation switch
    {
        VaultOperationKind.Sync => "正在同步保险库…",
        VaultOperationKind.SaveCipher => "正在保存条目…",
        VaultOperationKind.DeleteCipher => "正在更新回收站…",
        VaultOperationKind.Folder => "正在更新文件夹…",
        VaultOperationKind.Batch => "正在处理所选条目…",
        VaultOperationKind.AttachmentUpload => "正在上传附件…",
        VaultOperationKind.AttachmentDownload => "正在下载附件…",
        VaultOperationKind.AttachmentDelete => "正在删除附件…",
        _ => string.Empty,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    // InfoBar.IsOpen 需要 bool;OperationError 是 string,直接用转换器会因返回 Visibility 而绑定失败。
    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkippedCiphers))]
    [NotifyPropertyChangedFor(nameof(SkippedCipherMessage))]
    public partial int SkippedCipherCount { get; set; }

    // 同步时有条目无法解密被跳过 → 提示用户这些条目已被隐藏。
    public bool HasSkippedCiphers => SkippedCipherCount > 0;
    public string SkippedCipherMessage => $"{SkippedCipherCount} 个条目无法解密，已隐藏";

    // 当前快照来自离线缓存(未联网同步) → 提示用户数据可能已过期。
    [ObservableProperty]
    public partial bool DataStale { get; set; }

    public bool HasDetailSelected => Detail is not null;
    public bool NoSelection => Detail is null;

    // 详情要求主密码重提示且当前投影尚未解锁 → 显示"验证后显示敏感字段"入口。
    public bool ShowSensitiveUnlock => Detail is { Reprompt: true, SensitiveUnlocked: false };

    // 右栏“未选择任何项目”空状态浮层:无选中且不在编辑(新建条目时 Detail 为空但在编辑,需排除)。
    public bool ShowNoSelectionPlaceholder => Detail is null && !IsEditing;

    // 详情头 favicon 域名(仅登录详情有值)。
    public string? DetailIconDomain =>
        Detail is LoginDetail login ? Core.IconDomain.Extract(login.Uri) : null;
    public string? SelectedFilterTag => TagForFilter(SelectedFilter);
    public bool IsSelectedItemDeleted => SelectedItem?.IsDeleted == true;
    public bool IsFolderFilterSelected => SelectedFilter?.Kind == FilterKind.Folder;
    public bool IsTrashFilterSelected => SelectedFilter?.Kind == FilterKind.Trash;

    public int TrashCount => Items.Count(i => i.IsDeleted);
    public bool HasTrashItems => TrashCount > 0;
    public bool IsTrashEmpty => IsTrashFilterSelected && TrashCount == 0;
    public bool CanEmptyRecycleBin => IsTrashFilterSelected && HasTrashItems;
    public string TrashHeader => $"回收站 ({TrashCount})";

    // 保险库内一条(未删除)记录都没有 → 空库引导。
    public bool HasNoItems => Items.All(i => i.IsDeleted);
    // 库里有记录,但当前搜索/筛选结果为空 → 无结果提示。
    public bool NoResults => !HasNoItems && FilteredItems.Count == 0;

    public event EventHandler? FoldersChanged;

    public string EditorTitle => EditorDraft?.Type switch
    {
        VaultItemKind.Login => _editingId is null ? "新增登录" : "编辑登录",
        VaultItemKind.Card => _editingId is null ? "新增支付卡" : "编辑支付卡",
        VaultItemKind.Identity => _editingId is null ? "新增身份" : "编辑身份",
        VaultItemKind.Note => _editingId is null ? "新增笔记" : "编辑笔记",
        VaultItemKind.Ssh => _editingId is null ? "新增 SSH 密钥" : "编辑 SSH 密钥",
        _ => string.Empty,
    };

    public VaultViewModel(IVaultUiService service, IClipboardService? clipboard = null, IAttachmentUiService? attachments = null,
        ISavedSearchStore? savedStore = null, IItemRepromptGate? reprompt = null)
    {
        _service = service;
        _clipboard = clipboard;
        _attachments = attachments;
        _savedStore = savedStore;
        _reprompt = reprompt;
        foreach (var it in service.GetItems()) Items.Add(it);
        foreach (var f in service.GetFilters()) Filters.Add(f);
        SkippedCipherCount = service.SkippedCipherCount;
        DataStale = service.DataStale;
        if (savedStore is not null)
            foreach (var v in savedStore.GetAll()) SavedViews.Add(v);
        SelectedFilter = Filters.FirstOrDefault();
        ApplyFilter();
    }

    public SavedSearchView CaptureCurrentView(string name) =>
        new(name, SelectedFilterTag, CurrentFacets, SearchText, SelectedSort);

    [RelayCommand]
    private void SaveCurrentView(string name)
    {
        if (_savedStore is null || string.IsNullOrWhiteSpace(name)) return;
        var view = CaptureCurrentView(name.Trim());
        _savedStore.Save(view);
        var existing = SavedViews.FirstOrDefault(v => v.Name == view.Name);
        if (existing is not null) SavedViews.Remove(existing);
        SavedViews.Add(view);
    }

    [RelayCommand]
    private void ApplyView(SavedSearchView? view)
    {
        if (view is null) return;
        _suppressApply = true;
        try
        {
            SelectFilterByTag(view.NavTag);                 // 失效文件夹 → 回退 AllItems(既有行为)
            FacetTotp = view.Facets.Totp;
            FacetAttachment = view.Facets.Attachment;
            FacetUri = view.Facets.Uri;
            FacetFavoriteOnly = view.Facets.FavoriteOnly;
            SearchText = view.Search;
            SelectedSort = view.Sort;
        }
        finally { _suppressApply = false; }
        ApplyFilter();
    }

    [RelayCommand]
    private void DeleteView(string name)
    {
        if (_savedStore is null) return;
        _savedStore.Delete(name);
        var existing = SavedViews.FirstOrDefault(v => v.Name == name);
        if (existing is not null) SavedViews.Remove(existing);
    }

    partial void OnSelectedItemChanged(CipherListItem? value)
    {
        Detail = value is null ? null : ProjectDetail(value.Id);
        OnPropertyChanged(nameof(HasDetailSelected));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(IsSelectedItemDeleted));
        OnPropertyChanged(nameof(DetailIconDomain));
    }

    // 选中条目只投影安全视图:仅当条目级门禁已授权时才携带敏感字段,否则返回脱敏投影。
    private CipherDetail ProjectDetail(string id)
    {
        var policy = _service.GetAccessPolicy(id);
        var authorized = _reprompt?.IsAuthorized(policy) == true;
        return _service.GetDetail(id, includeSensitive: authorized);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_suppressApply) ApplyFilter();
        OnPropertyChanged(nameof(HasActiveRefinement));
    }

    public void SelectFilterByTag(string? tag)
    {
        SelectedFilter = FindFilterByTag(tag)
            ?? Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems)
            ?? Filters.FirstOrDefault();
    }

    private FilterNode? FindFilterByTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        return tag switch
        {
            "vault:allitems" => Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems),
            "vault:favorites" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Favorites),
            "vault:trash" => Filters.FirstOrDefault(f => f.Kind == FilterKind.Trash),
            _ when tag.StartsWith("vault:type:", StringComparison.Ordinal) => FindTypeFilter(tag["vault:type:".Length..]),
            _ when tag.StartsWith("vault:folder:", StringComparison.Ordinal) => FindFolderFilter(tag["vault:folder:".Length..]),
            _ => null,
        };
    }

    private FilterNode? FindTypeFilter(string typeName) =>
        Enum.TryParse(typeName, out VaultItemKind type)
            ? Filters.FirstOrDefault(f => f.Kind == FilterKind.Type && f.TypeFilter == type)
            : null;

    private FilterNode? FindFolderFilter(string folderId) =>
        Filters.FirstOrDefault(f => f.Kind == FilterKind.Folder && f.FolderId == folderId);

    partial void OnSelectedFilterChanged(FilterNode? value)
    {
        if (!_suppressApply) ApplyFilter();
        OnPropertyChanged(nameof(SelectedFilterTag));
        OnPropertyChanged(nameof(IsFolderFilterSelected));
        OnPropertyChanged(nameof(IsTrashFilterSelected));
    }

    private static string? TagForFilter(FilterNode? filter) => filter?.Kind switch
    {
        FilterKind.AllItems => "vault:allitems",
        FilterKind.Favorites => "vault:favorites",
        FilterKind.Trash => "vault:trash",
        FilterKind.Type when filter.TypeFilter is not null => $"vault:type:{filter.TypeFilter}",
        FilterKind.Folder when !string.IsNullOrWhiteSpace(filter.FolderId) => $"vault:folder:{filter.FolderId}",
        _ => null,
    };

    private void ApplyFilter()
    {
        FilteredItems.Clear();

        IEnumerable<CipherListItem> baseItems = SelectedFilter?.Kind switch
        {
            FilterKind.Favorites => Items.Where(i => i.Favorite && !i.IsDeleted),
            FilterKind.Trash     => Items.Where(i => i.IsDeleted),
            FilterKind.Type      => Items.Where(i => i.Kind == SelectedFilter.TypeFilter && !i.IsDeleted),
            FilterKind.Folder    => Items.Where(i => i.FolderId == SelectedFilter.FolderId && !i.IsDeleted),
            _                    => Items.Where(i => !i.IsDeleted), // AllItems 及未选中
        };

        foreach (var i in VaultQuery.Apply(baseItems, SearchText, CurrentFacets, SelectedSort))
            FilteredItems.Add(i);

        RebuildGroups();
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(HasActiveRefinement));
        OnPropertyChanged(nameof(TrashCount));
        OnPropertyChanged(nameof(HasTrashItems));
        OnPropertyChanged(nameof(IsTrashEmpty));
        OnPropertyChanged(nameof(CanEmptyRecycleBin));
        OnPropertyChanged(nameof(TrashHeader));
    }

    private static readonly VaultItemKind[] TypeOrder =
        { VaultItemKind.Login, VaultItemKind.Card, VaultItemKind.Identity, VaultItemKind.Note, VaultItemKind.Ssh };

    private static string TypeDisplayName(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Login => "登录",
        VaultItemKind.Card => "银行卡",
        VaultItemKind.Identity => "身份",
        VaultItemKind.Note => "笔记",
        VaultItemKind.Ssh => "SSH 密钥",
        _ => "其他",
    };

    private string FolderNameFor(string? folderId)
    {
        if (string.IsNullOrEmpty(folderId))
            return "无文件夹";
        var folder = Filters.FirstOrDefault(f => f.Kind == FilterKind.Folder && f.FolderId == folderId);
        return folder?.Label ?? "无文件夹";
    }

    private void RebuildGroups()
    {
        GroupedItems.Clear();

        // 日期排序:压平为单一无头分组(跨文件夹/类型分组无意义)。
        if (SelectedSort is VaultSortKey.RevisionDesc or VaultSortKey.CreationDesc)
        {
            if (FilteredItems.Count == 0) return;
            var flat = new VaultListGroup { Key = string.Empty, ShowHeader = false };
            foreach (var i in FilteredItems) flat.Add(i);
            GroupedItems.Add(flat);
            return;
        }

        // 选具体文件夹:单组、不显头。
        if (SelectedFilter?.Kind == FilterKind.Folder)
        {
            if (FilteredItems.Count == 0)
                return;
            var single = new VaultListGroup { Key = SelectedFilter.Label, ShowHeader = false };
            foreach (var i in FilteredItems) single.Add(i);
            GroupedItems.Add(single);
            return;
        }

        // 选具体类型:按文件夹分组,"无文件夹"置末,其余按名称。
        if (SelectedFilter?.Kind == FilterKind.Type)
        {
            var byFolder = FilteredItems
                .GroupBy(i => i.FolderId)
                .OrderBy(g => string.IsNullOrEmpty(g.Key) ? 1 : 0)
                .ThenBy(g => FolderNameFor(g.Key), StringComparer.CurrentCulture);
            foreach (var g in byFolder)
            {
                var group = new VaultListGroup { Key = FolderNameFor(g.Key), ShowHeader = true };
                foreach (var i in g) group.Add(i);
                GroupedItems.Add(group);
            }
            return;
        }

        // 聚合视图:按类型分组,固定组序。
        foreach (var kind in TypeOrder)
        {
            var items = FilteredItems.Where(i => i.Kind == kind).ToList();
            if (items.Count == 0)
                continue;
            var group = new VaultListGroup { Key = TypeDisplayName(kind), ShowHeader = true };
            foreach (var i in items) group.Add(i);
            GroupedItems.Add(group);
        }
    }

    public void BeginAdd(VaultItemKind type)
    {
        _editingId = null;
        EditorDraft = CipherEditorDraft.CreateDefault(type);
        EditorError = string.Empty;
        OperationError = string.Empty;
        IsEditing = true;
        SelectedItem = null;
        Detail = null;
        OnPropertyChanged(nameof(HasDetailSelected));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(DetailIconDomain));
        OnPropertyChanged(nameof(EditorTitle));
    }

    // 统一条目级授权:Reprompt==false 直接通过;缺失门禁服务 fail-closed;否则走门禁验证。
    // 授权结果在 await 后由门禁自身用 lease 校验会话未变;调用方仅在成功后才读取/绑定秘密。
    private async Task<UiOperationResult<bool>> EnsureAuthorizedAsync(
        string id,
        SensitiveItemAction action,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct)
    {
        var policy = _service.GetAccessPolicy(id);
        if (!policy.RequiresReprompt)
            return UiOperationResult<bool>.Success(true);
        if (_reprompt is null)
            return UiOperationResult<bool>.Failure("敏感条目验证服务不可用");
        return await _reprompt.AuthorizeAsync(policy, action, prompt, ct);
    }

    // 编辑必须先过门禁再读 draft:未授权时绝不构造/暴露可编辑的明文草稿。
    public async Task<UiOperationResult> BeginEditAsync(
        string id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var authorization = await EnsureAuthorizedAsync(
            id, SensitiveItemAction.Edit, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult(authorization.Status, authorization.Error);

        EditorDraft = _service.GetDraft(id);
        _editingId = id;
        EditorError = string.Empty;
        OperationError = string.Empty;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
        return UiOperationResult.Success();
    }

    public void CancelEdit()
    {
        IsEditing = false;
        EditorDraft = null;
        EditorError = string.Empty;
        OperationError = string.Empty;
        _editingId = null;
        OnPropertyChanged(nameof(EditorTitle));
    }

    public void ChangeEditorType(VaultItemKind type)
    {
        if (EditorDraft is null)
            return;

        EditorDraft.Type = type;
        EditorError = string.Empty;
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorDraft));
    }

    // 唯一 Vault 操作 runner:_operationGate 原子去重(第二个并发调用立即返回 Busy,不触发 action);
    // 取消经 _operationCts 与调用方 ct 联动;已知业务异常映射为安全文案,未知异常不捕获(交由 Task 12)。
    private async Task<UiOperationResult<T>> RunOperationAsync<T>(
        VaultOperationKind kind,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        if (!_operationGate.TryEnter(out var operation))
            return UiOperationResult<T>.Busy();

        using (operation)
        {
            _operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CurrentOperation = kind;
            OperationError = string.Empty;
            try
            {
                return UiOperationResult<T>.Success(await action(_operationCts.Token));
            }
            catch (OperationCanceledException) when (_operationCts.IsCancellationRequested)
            {
                return UiOperationResult<T>.Cancelled();
            }
            catch (Exception ex) when (ex is
                Core.Services.VaultRefreshException
                or Core.Services.AttachmentTooLargeException
                or Api.VaultWriteException
                or HttpRequestException)
            {
                OperationError = SafeOperationMessage(ex);
                return UiOperationResult<T>.Failure(OperationError);
            }
            finally
            {
                _operationCts.Dispose();
                _operationCts = null;
                CurrentOperation = null;
            }
        }
    }

    private static string SafeOperationMessage(Exception ex) => ex switch
    {
        Core.Services.VaultRefreshException => "更改已保存，但本地密码库刷新失败，请手动同步。",
        Core.Services.AttachmentTooLargeException => ex.Message,
        Api.VaultWriteException => "服务器拒绝了此更改，请检查输入后重试。",
        HttpRequestException => "网络请求失败，请检查连接后重试。",
        _ => "操作失败。",
    };

    /// <summary>取消当前正在进行的 Vault 操作(若有)。用于生命周期清理与用户主动取消,不影响后续新操作进入门禁。</summary>
    public void CancelCurrentOperation() => _operationCts?.Cancel();

    public async Task<UiOperationResult<string>> SaveDraftAsync(CancellationToken ct = default)
    {
        if (EditorDraft is null)
            return UiOperationResult<string>.Failure("没有可保存的草稿");
        var errors = EditorDraft.Validate();
        if (errors.Count > 0)
        {
            EditorError = string.Join(Environment.NewLine, errors);
            return UiOperationResult<string>.Failure(EditorError);
        }

        var draft = EditorDraft;
        var editingId = _editingId;
        return await RunOperationAsync(
            VaultOperationKind.SaveCipher,
            async token =>
            {
                var savedId = await _service.SaveCipherAsync(draft, editingId, token);
                ReloadItems();
                ReloadFilters();
                SelectById(savedId);
                CancelEdit();
                return savedId;
            },
            ct);
    }

    private async Task<UiOperationResult> RunWriteAndReloadAsync(
        VaultOperationKind kind,
        Func<CancellationToken, Task> operation,
        string? selectId,
        CancellationToken ct)
    {
        var result = await RunOperationAsync(
            kind,
            async token =>
            {
                await operation(token);
                ReloadItems();
                ReloadFilters();
                SelectById(selectId);
                return true;
            },
            ct);
        return new UiOperationResult(result.Status, result.Error);
    }

    public Task<UiOperationResult> SoftDeleteAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.DeleteCipher,
            token => _service.DeleteCipherAsync(id, permanent: false, token),
            selectId: null,
            ct);

    public Task<UiOperationResult> PermanentDeleteAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.DeleteCipher,
            token => _service.DeleteCipherAsync(id, permanent: true, token),
            selectId: null,
            ct);

    public Task<UiOperationResult> RestoreAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.DeleteCipher,
            token => _service.RestoreCipherAsync(id, token),
            selectId: id,
            ct);

    public async Task<UiOperationResult> SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return UiOperationResult.Failure("文件夹名称不能为空。");
        var result = await RunWriteAndReloadAsync(
            VaultOperationKind.Folder,
            token => _service.SaveFolderAsync(folderId, name.Trim(), token),
            SelectedItem?.Id,
            ct);
        if (result.IsSuccess)
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task<UiOperationResult> DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        var result = await RunWriteAndReloadAsync(
            VaultOperationKind.Folder,
            token => _service.DeleteFolderAsync(folderId, token),
            SelectedItem?.Id,
            ct);
        if (result.IsSuccess)
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    // 批量操作共用 helper:从 _selectedIds 取快照;空选择直接失败,不占用门禁、不触发服务调用。
    private Task<UiOperationResult> RunBatchAsync(
        Func<IReadOnlyCollection<string>, CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        var ids = _selectedIds.ToArray();
        if (ids.Length == 0)
            return Task.FromResult(UiOperationResult.Failure("没有选择任何条目。"));
        return RunWriteAndReloadAsync(
            VaultOperationKind.Batch,
            token => operation(ids, token),
            selectId: null,
            ct);
    }

    private void ReloadItems()
    {
        Items.Clear();
        foreach (var item in _service.GetItems())
            Items.Add(item);
    }

    private void ReloadFilters()
    {
        var tag = SelectedFilterTag;
        Filters.Clear();
        foreach (var filter in _service.GetFilters())
            Filters.Add(filter);
        OnPropertyChanged(nameof(FolderFilters));
        SelectFilterByTag(tag); // 复原选中(凭 tag),触发 ApplyFilter
    }

    private void SelectById(string? id)
    {
        ApplyFilter();
        if (string.IsNullOrEmpty(id))
        {
            SelectedItem = null;
            return;
        }

        var item = FilteredItems.FirstOrDefault(i => i.Id == id);
        if (item is null)
        {
            SearchText = string.Empty;
            SelectedFilter = Filters.FirstOrDefault(f => f.Kind == FilterKind.AllItems) ?? Filters.FirstOrDefault();
            item = FilteredItems.FirstOrDefault(i => i.Id == id);
        }
        SelectedItem = item;
    }

    public async Task<UiOperationResult> SyncAsync(CancellationToken ct = default)
    {
        var selectedId = SelectedItem?.Id;
        var result = await RunOperationAsync(
            VaultOperationKind.Sync,
            async token =>
            {
                await _service.SyncAsync(token);
                ReloadItems();
                ReloadFilters();
                SelectById(selectedId);
                return true;
            },
            ct);
        return new UiOperationResult(result.Status, result.Error);
    }

    [RelayCommand]
    private Task Sync() => SyncAsync();

    /// <summary>
    /// 从服务快照刷新列表（不发起网络请求），供 WebSocket 推送后 UI 增量刷新使用。
    /// </summary>
    public void RefreshFromSnapshot()
    {
        ReloadItems();
        ReloadFilters();
        // 保持当前选中项（若已被删除则自动清空）
        SelectById(SelectedItem?.Id);
        SkippedCipherCount = _service.SkippedCipherCount;
        DataStale = _service.DataStale;
    }

    [RelayCommand]
    private void Add() => BeginAdd(VaultItemKind.Login);

    public void ToggleSelection(string id)
    {
        if (!_selectedIds.Remove(id))
            _selectedIds.Add(id);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    // 由列表的 SelectionChanged 逐项调用:把 ListView 的勾选/取消精确同步进 _selectedIds
    // (此前 UI 从不同步,逐项勾选完全不被记录,批量操作全程禁用)。
    public void SetItemSelected(string id, bool selected)
    {
        var changed = selected ? _selectedIds.Add(id) : _selectedIds.Remove(id);
        if (!changed)
            return;
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    public bool IsSelected(string id) => _selectedIds.Contains(id);

    private void ClearSelection()
    {
        _selectedIds.Clear();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    // 锁屏/登出时清空内存中的明文足迹:选中项、详情、编辑草稿、搜索与全部条目/过滤集合。
    // 先取消当前 Vault 操作(尽快退出,不等待网络往返),lease 失效仍是最终提交保护。
    // 幂等:重复调用安全(集合已空、属性已置默认值)。
    public void ClearSensitive()
    {
        CancelCurrentOperation();
        SelectedItem = null;
        Detail = null;
        EditorDraft = null;
        _editingId = null;
        IsEditing = false;
        SearchText = string.Empty;
        OperationError = string.Empty;
        EditorError = string.Empty;
        ClearSelection();
        Items.Clear();
        FilteredItems.Clear();
        GroupedItems.Clear();
        Filters.Clear();
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
            ClearSelection(); // ClearSelection 已通知 HasSelection
        else
        {
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

    [RelayCommand]
    private void SelectAll()
    {
        if (!IsSelectionMode)
            return;
        foreach (var item in FilteredItems)
            _selectedIds.Add(item.Id);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private async Task MoveSelectedToFolder(string? folderId)
    {
        var result = await RunBatchAsync((ids, ct) => _service.MoveCiphersAsync(ids, folderId, ct));
        if (result.IsSuccess)
            IsSelectionMode = false; // OnIsSelectionModeChanged clears selection
    }

    public Task MoveSelectedToFolderAsync(string? folderId) => MoveSelectedToFolder(folderId);

    [RelayCommand]
    private async Task SoftDeleteSelected()
    {
        var result = await RunBatchAsync((ids, ct) => _service.DeleteCiphersAsync(ids, permanent: false, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        var result = await RunBatchAsync((ids, ct) => _service.RestoreCiphersAsync(ids, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    // 注：破坏性确认在 XAML code-behind 先弹 ContentDialog，确认后才执行此命令。
    [RelayCommand]
    private async Task PermanentDeleteSelected()
    {
        var result = await RunBatchAsync((ids, ct) => _service.DeleteCiphersAsync(ids, permanent: true, ct));
        if (result.IsSuccess)
            IsSelectionMode = false;
    }

    // 注：清空确认在 XAML code-behind 先弹 ContentDialog，确认后才执行此命令。清空回收站不依赖
    // 选择集(操作全部已删除条目),故不经 RunBatchAsync,直接复用 RunWriteAndReloadAsync。
    [RelayCommand]
    private Task<UiOperationResult> EmptyRecycleBinAsync()
    {
        var ids = Items.Where(i => i.IsDeleted).Select(i => i.Id).ToArray();
        if (ids.Length == 0)
            return Task.FromResult(UiOperationResult.Failure("回收站为空。"));
        return RunWriteAndReloadAsync(
            VaultOperationKind.Batch,
            token => _service.DeleteCiphersAsync(ids, permanent: true, token),
            selectId: null,
            CancellationToken.None);
    }

    [RelayCommand]
    private void AddCustomField()
    {
        var draft = EditorDraft;
        if (draft is null)
            return;

        draft.CustomFields.Add(new CustomFieldEditorDraft
        {
            Name = $"字段 {draft.CustomFields.Count + 1}",
        });
    }

    [RelayCommand]
    private void RemoveCustomField(CustomFieldEditorDraft? field)
    {
        if (EditorDraft is null || field is null)
            return;

        EditorDraft.CustomFields.Remove(field);
    }

    [RelayCommand]
    private void Copy(string? value)
    {
        if (!string.IsNullOrEmpty(value)) _clipboard?.SetText(value);
    }

    // 复制主值必须先过条目级门禁:未授权/被拒时绝不读取敏感详情、绝不写剪贴板。
    public async Task<UiOperationResult> CopyPrimaryAsync(
        string? id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id))
            return UiOperationResult.Failure("无效条目");

        var authorization = await EnsureAuthorizedAsync(
            id, SensitiveItemAction.Copy, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult(authorization.Status, authorization.Error);

        var value = PrimaryValue(_service.GetDetail(id, includeSensitive: true));
        if (!string.IsNullOrEmpty(value))
            _clipboard?.SetSecretText(value);
        return UiOperationResult.Success();
    }

    // 各类型主值:登录→密码(空则用户名),卡→卡号,笔记→内容,SSH→公钥,身份等→名称。
    private static string? PrimaryValue(CipherDetail detail) => detail switch
    {
        LoginDetail l => string.IsNullOrEmpty(l.Password) ? l.Username : l.Password,
        CardDetail c => c.Number,
        NoteDetail n => n.Content,
        SshDetail s => s.PublicKey,
        _ => detail.Name,
    };

    // ── 条目级主密码重提示门禁 ───────────────────────────────────────────────────
    // 解锁当前详情的敏感字段:先过条目级门禁(动作 Totp——授权前 TotpSecret 恒为 null),
    // 成功后才用 includeSensitive 重投影 Detail。被拒/取消时 Detail 维持脱敏投影不变。
    public async Task<UiOperationResult<bool>> UnlockDetailAsync(
        string id,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var authorization = await EnsureAuthorizedAsync(
            id, SensitiveItemAction.Totp, prompt, ct);
        if (!authorization.IsSuccess)
            return authorization;

        if (SelectedItem?.Id == id || Detail?.Id == id)
        {
            Detail = _service.GetDetail(id, includeSensitive: true);
            OnPropertyChanged(nameof(DetailIconDomain));
        }
        return UiOperationResult<bool>.Success(true);
    }

    public Task<UiOperationResult> ToggleFavoriteAsync(string id, CancellationToken ct = default) =>
        RunWriteAndReloadAsync(
            VaultOperationKind.SaveCipher,
            async token =>
            {
                var draft = _service.GetDraft(id);
                draft.Favorite = !draft.Favorite;
                await _service.SaveCipherAsync(draft, id, token);
            },
            selectId: id,
            ct);

    public async Task<UiOperationResult> AddAttachmentAsync(string cipherId, byte[] fileBytes, string fileName, CancellationToken ct = default)
    {
        if (_attachments is null)
            return UiOperationResult.Failure("附件服务不可用");

        var result = await RunOperationAsync(
            VaultOperationKind.AttachmentUpload,
            async token =>
            {
                var attachments = await _attachments.AddAttachmentAsync(cipherId, fileBytes, fileName, token);
                RefreshDetailAttachments(cipherId, attachments);
                return true;
            },
            ct);
        return new UiOperationResult(result.Status, result.Error);
    }

    // 下载附件前必须先过条目级门禁:被拒/取消时绝不调用附件服务、绝不占用操作门禁。
    // 门禁原子去重交由 RunOperationAsync 的 TryEnter 完成,无需额外 IsBusy 预检查。
    public async Task<UiOperationResult<byte[]>> DownloadAttachmentAsync(
        string cipherId,
        string attachmentId,
        Func<CancellationToken, Task<string?>> prompt,
        CancellationToken ct = default)
    {
        var authorization = await EnsureAuthorizedAsync(
            cipherId, SensitiveItemAction.DownloadAttachment, prompt, ct);
        if (!authorization.IsSuccess)
            return new UiOperationResult<byte[]>(authorization.Status, default, authorization.Error);
        if (_attachments is null)
            return UiOperationResult<byte[]>.Failure("附件服务不可用");

        return await RunOperationAsync(
            VaultOperationKind.AttachmentDownload,
            token => _attachments.DownloadAttachmentAsync(cipherId, attachmentId, token),
            ct);
    }

    public async Task<UiOperationResult> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        if (_attachments is null)
            return UiOperationResult.Failure("附件服务不可用");

        var result = await RunOperationAsync(
            VaultOperationKind.AttachmentDelete,
            async token =>
            {
                var attachments = await _attachments.DeleteAttachmentAsync(cipherId, attachmentId, token);
                RefreshDetailAttachments(cipherId, attachments);
                return true;
            },
            ct);
        return new UiOperationResult(result.Status, result.Error);
    }

    // 当前选中详情就是被改动的条目时,用最新附件列表重投影 Detail。
    // CipherDetail.Attachments 是 init-only,故用 _service.GetDetail 重建详情(re-sync 后快照已含新附件),
    // 再以服务刚返回的 attachments 覆盖,避免快照/服务返回不一致时显示陈旧数据。
    private void RefreshDetailAttachments(string cipherId, IReadOnlyList<AttachmentItem> attachments)
    {
        if (Detail is null || Detail.Id != cipherId)
            return;

        // 保留当前解锁状态重投影:锁定详情仍脱敏,已解锁详情仍显示敏感字段。
        var includeSensitive = Detail.SensitiveUnlocked;
        Detail = _service.GetDetail(cipherId, includeSensitive) is { } refreshed && refreshed.Id == cipherId
            ? CloneDetailWithAttachments(refreshed, attachments)
            : Detail;
        OnPropertyChanged(nameof(DetailIconDomain));
    }

    private static CipherDetail CloneDetailWithAttachments(CipherDetail detail, IReadOnlyList<AttachmentItem> attachments) => detail switch
    {
        LoginDetail l => new LoginDetail
        {
            Id = l.Id, Name = l.Name, FolderName = l.FolderName, Notes = l.Notes, CustomFields = l.CustomFields,
            Created = l.Created, Edited = l.Edited, IsDeleted = l.IsDeleted, Favorite = l.Favorite, Reprompt = l.Reprompt,
            SensitiveUnlocked = l.SensitiveUnlocked,
            Username = l.Username, Password = l.Password, TotpSecret = l.TotpSecret, Uri = l.Uri, Passkeys = l.Passkeys,
            PasswordHistory = l.PasswordHistory,
            Attachments = attachments,
        },
        CardDetail c => new CardDetail
        {
            Id = c.Id, Name = c.Name, FolderName = c.FolderName, Notes = c.Notes, CustomFields = c.CustomFields,
            Created = c.Created, Edited = c.Edited, IsDeleted = c.IsDeleted, Favorite = c.Favorite, Reprompt = c.Reprompt,
            SensitiveUnlocked = c.SensitiveUnlocked,
            Cardholder = c.Cardholder, Number = c.Number, Expiry = c.Expiry, Brand = c.Brand, Cvv = c.Cvv,
            Attachments = attachments,
        },
        IdentityDetail i => new IdentityDetail
        {
            Id = i.Id, Name = i.Name, FolderName = i.FolderName, Notes = i.Notes, CustomFields = i.CustomFields,
            Created = i.Created, Edited = i.Edited, IsDeleted = i.IsDeleted, Favorite = i.Favorite, Reprompt = i.Reprompt,
            SensitiveUnlocked = i.SensitiveUnlocked,
            FullName = i.FullName, Username = i.Username, Company = i.Company, Email = i.Email, Phone = i.Phone,
            IdNumber = i.IdNumber, Address = i.Address,
            Attachments = attachments,
        },
        NoteDetail n => new NoteDetail
        {
            Id = n.Id, Name = n.Name, FolderName = n.FolderName, Notes = n.Notes, CustomFields = n.CustomFields,
            Created = n.Created, Edited = n.Edited, IsDeleted = n.IsDeleted, Favorite = n.Favorite, Reprompt = n.Reprompt,
            SensitiveUnlocked = n.SensitiveUnlocked,
            Content = n.Content,
            Attachments = attachments,
        },
        SshDetail s => new SshDetail
        {
            Id = s.Id, Name = s.Name, FolderName = s.FolderName, Notes = s.Notes, CustomFields = s.CustomFields,
            Created = s.Created, Edited = s.Edited, IsDeleted = s.IsDeleted, Favorite = s.Favorite, Reprompt = s.Reprompt,
            SensitiveUnlocked = s.SensitiveUnlocked,
            PublicKey = s.PublicKey, PrivateKey = s.PrivateKey, Fingerprint = s.Fingerprint,
            Attachments = attachments,
        },
        _ => detail,
    };
}
