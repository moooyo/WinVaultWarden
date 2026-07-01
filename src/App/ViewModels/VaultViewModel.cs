using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

public partial class VaultViewModel : ObservableObject
{
    private readonly IVaultUiService _service;
    private readonly IClipboardService? _clipboard;
    private readonly IAttachmentUiService? _attachments;
    private string? _editingId;
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);

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
    public partial CipherDetail? Detail { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FacetTotp { get; set; }
    [ObservableProperty]
    public partial bool FacetAttachment { get; set; }
    [ObservableProperty]
    public partial bool FacetUri { get; set; }
    [ObservableProperty]
    public partial bool FacetFavoriteOnly { get; set; }
    [ObservableProperty]
    public partial VaultSortKey SelectedSort { get; set; } = VaultSortKey.NameAsc;

    public VaultFacets CurrentFacets => new(FacetTotp, FacetAttachment, FacetUri, FacetFavoriteOnly);
    public bool HasActiveRefinement => CurrentFacets.Any || !string.IsNullOrWhiteSpace(SearchText);

    partial void OnFacetTotpChanged(bool value) => ApplyFilter();
    partial void OnFacetAttachmentChanged(bool value) => ApplyFilter();
    partial void OnFacetUriChanged(bool value) => ApplyFilter();
    partial void OnFacetFavoriteOnlyChanged(bool value) => ApplyFilter();
    partial void OnSelectedSortChanged(VaultSortKey value) => ApplyFilter();

    [ObservableProperty]
    public partial FilterNode? SelectedFilter { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial CipherEditorDraft? EditorDraft { get; set; }

    [ObservableProperty]
    public partial string EditorError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    // InfoBar.IsOpen 需要 bool;OperationError 是 string,直接用转换器会因返回 Visibility 而绑定失败。
    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

    public bool HasDetailSelected => Detail is not null;
    public bool NoSelection => Detail is null;

    // 详情头 favicon 域名(仅登录详情有值)。
    public string? DetailIconDomain =>
        Detail is LoginDetail login ? Core.IconDomain.Extract(login.Uri) : null;
    public string? SelectedFilterTag => TagForFilter(SelectedFilter);
    public bool IsSelectedItemDeleted => SelectedItem?.IsDeleted == true;
    public bool IsFolderFilterSelected => SelectedFilter?.Kind == FilterKind.Folder;
    public bool IsTrashFilterSelected => SelectedFilter?.Kind == FilterKind.Trash;

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

    public VaultViewModel(IVaultUiService service, IClipboardService? clipboard = null, IAttachmentUiService? attachments = null)
    {
        _service = service;
        _clipboard = clipboard;
        _attachments = attachments;
        foreach (var it in service.GetItems()) Items.Add(it);
        foreach (var f in service.GetFilters()) Filters.Add(f);
        SelectedFilter = Filters.FirstOrDefault();
        ApplyFilter();
    }

    partial void OnSelectedItemChanged(CipherListItem? value)
    {
        Detail = value is null ? null : _service.GetDetail(value.Id);
        OnPropertyChanged(nameof(HasDetailSelected));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(IsSelectedItemDeleted));
        OnPropertyChanged(nameof(DetailIconDomain));
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
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
        ApplyFilter();
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
            foreach (var i in FilteredItems) flat.Items.Add(i);
            GroupedItems.Add(flat);
            return;
        }

        // 选具体文件夹:单组、不显头。
        if (SelectedFilter?.Kind == FilterKind.Folder)
        {
            if (FilteredItems.Count == 0)
                return;
            var single = new VaultListGroup { Key = SelectedFilter.Label, ShowHeader = false };
            foreach (var i in FilteredItems) single.Items.Add(i);
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
                foreach (var i in g) group.Items.Add(i);
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
            foreach (var i in items) group.Items.Add(i);
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

    public void BeginEdit(string id)
    {
        EditorDraft = _service.GetDraft(id);
        _editingId = id;
        EditorError = string.Empty;
        OperationError = string.Empty;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
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

    public async Task<bool> SaveDraftAsync()
    {
        if (EditorDraft is null || IsBusy)
            return false;

        var draft = EditorDraft;
        var errors = draft.Validate();
        if (errors.Count > 0)
        {
            EditorError = string.Join(Environment.NewLine, errors);
            return false;
        }

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var savedId = await _service.SaveCipherAsync(draft, _editingId);
            ReloadItems();
            ReloadFilters();
            SelectById(savedId);
            IsEditing = false;
            EditorDraft = null;
            EditorError = string.Empty;
            _editingId = null;
            OnPropertyChanged(nameof(EditorTitle));
            return true;
        }
        catch (Exception ex)
        {
            OperationError = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<bool> SoftDeleteAsync(string id) =>
        RunWriteAsync(() => _service.DeleteCipherAsync(id, permanent: false), selectId: null);

    public Task<bool> PermanentDeleteAsync(string id) =>
        RunWriteAsync(() => _service.DeleteCipherAsync(id, permanent: true), selectId: null);

    public Task<bool> RestoreAsync(string id) =>
        RunWriteAsync(() => _service.RestoreCipherAsync(id), selectId: id);

    public async Task<bool> SaveFolderAsync(string? folderId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var ok = await RunWriteAsync(() => _service.SaveFolderAsync(folderId, name.Trim()), selectId: SelectedItem?.Id);
        if (ok)
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public async Task<bool> DeleteFolderAsync(string folderId)
    {
        var ok = await RunWriteAsync(() => _service.DeleteFolderAsync(folderId), selectId: SelectedItem?.Id);
        if (ok)
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    private async Task<bool> RunWriteAsync(Func<Task> operation, string? selectId)
    {
        if (IsBusy)
            return false;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await operation();
            ReloadItems();
            ReloadFilters();
            SelectById(selectId);
            return true;
        }
        catch (Exception ex)
        {
            OperationError = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
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

    [RelayCommand]
    private async Task Sync()
    {
        await RunWriteAsync(() => _service.SyncAsync(), selectId: SelectedItem?.Id);
    }

    /// <summary>
    /// 从服务快照刷新列表（不发起网络请求），供 WebSocket 推送后 UI 增量刷新使用。
    /// </summary>
    public void RefreshFromSnapshot()
    {
        ReloadItems();
        ReloadFilters();
        // 保持当前选中项（若已被删除则自动清空）
        SelectById(SelectedItem?.Id);
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

    public bool IsSelected(string id) => _selectedIds.Contains(id);

    private void ClearSelection()
    {
        _selectedIds.Clear();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
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
        if (_selectedIds.Count == 0)
            return;
        var ids = _selectedIds.ToArray();
        var ok = await RunWriteAsync(() => _service.MoveCiphersAsync(ids, folderId), selectId: null);
        if (ok)
            IsSelectionMode = false; // OnIsSelectionModeChanged clears selection
    }

    public Task MoveSelectedToFolderAsync(string? folderId) => MoveSelectedToFolder(folderId);

    [RelayCommand]
    private async Task SoftDeleteSelected()
    {
        if (_selectedIds.Count == 0)
            return;
        var ids = _selectedIds.ToArray();
        var ok = await RunWriteAsync(() => _service.DeleteCiphersAsync(ids, permanent: false), selectId: null);
        if (ok)
            IsSelectionMode = false;
    }

    [RelayCommand]
    private async Task RestoreSelected()
    {
        if (_selectedIds.Count == 0)
            return;
        var ids = _selectedIds.ToArray();
        var ok = await RunWriteAsync(() => _service.RestoreCiphersAsync(ids), selectId: null);
        if (ok)
            IsSelectionMode = false;
    }

    // 注：破坏性确认在 XAML code-behind 先弹 ContentDialog，确认后才执行此命令。
    [RelayCommand]
    private async Task PermanentDeleteSelected()
    {
        if (_selectedIds.Count == 0)
            return;
        var ids = _selectedIds.ToArray();
        var ok = await RunWriteAsync(() => _service.DeleteCiphersAsync(ids, permanent: true), selectId: null);
        if (ok)
            IsSelectionMode = false;
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

    [RelayCommand]
    private void CopyPrimary(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return;
        var value = PrimaryValue(_service.GetDetail(id));
        if (!string.IsNullOrEmpty(value))
            _clipboard?.SetSecretText(value);
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

    public Task<bool> ToggleFavoriteAsync(string id) =>
        RunWriteAsync(async () =>
        {
            var draft = _service.GetDraft(id);
            draft.Favorite = !draft.Favorite;
            await _service.SaveCipherAsync(draft, id);
        }, selectId: id);

    public async Task AddAttachmentAsync(string cipherId, byte[] fileBytes, string fileName, CancellationToken ct = default)
    {
        if (_attachments is null || IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var attachments = await _attachments.AddAttachmentAsync(cipherId, fileBytes, fileName, ct);
            RefreshDetailAttachments(cipherId, attachments);
        }
        catch (Exception ex)
        {
            OperationError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<byte[]?> DownloadAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        if (_attachments is null || IsBusy)
            return null;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            return await _attachments.DownloadAttachmentAsync(cipherId, attachmentId, ct);
        }
        catch (Exception ex)
        {
            OperationError = ex.Message;
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default)
    {
        if (_attachments is null || IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var attachments = await _attachments.DeleteAttachmentAsync(cipherId, attachmentId, ct);
            RefreshDetailAttachments(cipherId, attachments);
        }
        catch (Exception ex)
        {
            OperationError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 当前选中详情就是被改动的条目时,用最新附件列表重投影 Detail。
    // CipherDetail.Attachments 是 init-only,故用 _service.GetDetail 重建详情(re-sync 后快照已含新附件),
    // 再以服务刚返回的 attachments 覆盖,避免快照/服务返回不一致时显示陈旧数据。
    private void RefreshDetailAttachments(string cipherId, IReadOnlyList<AttachmentItem> attachments)
    {
        if (Detail is null || Detail.Id != cipherId)
            return;

        Detail = _service.GetDetail(cipherId) is { } refreshed && refreshed.Id == cipherId
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
            Username = l.Username, Password = l.Password, TotpSecret = l.TotpSecret, Uri = l.Uri, Passkeys = l.Passkeys,
            Attachments = attachments,
        },
        CardDetail c => new CardDetail
        {
            Id = c.Id, Name = c.Name, FolderName = c.FolderName, Notes = c.Notes, CustomFields = c.CustomFields,
            Created = c.Created, Edited = c.Edited, IsDeleted = c.IsDeleted, Favorite = c.Favorite, Reprompt = c.Reprompt,
            Cardholder = c.Cardholder, Number = c.Number, Expiry = c.Expiry, Brand = c.Brand, Cvv = c.Cvv,
            Attachments = attachments,
        },
        IdentityDetail i => new IdentityDetail
        {
            Id = i.Id, Name = i.Name, FolderName = i.FolderName, Notes = i.Notes, CustomFields = i.CustomFields,
            Created = i.Created, Edited = i.Edited, IsDeleted = i.IsDeleted, Favorite = i.Favorite, Reprompt = i.Reprompt,
            FullName = i.FullName, Username = i.Username, Company = i.Company, Email = i.Email, Phone = i.Phone,
            IdNumber = i.IdNumber, Address = i.Address,
            Attachments = attachments,
        },
        NoteDetail n => new NoteDetail
        {
            Id = n.Id, Name = n.Name, FolderName = n.FolderName, Notes = n.Notes, CustomFields = n.CustomFields,
            Created = n.Created, Edited = n.Edited, IsDeleted = n.IsDeleted, Favorite = n.Favorite, Reprompt = n.Reprompt,
            Content = n.Content,
            Attachments = attachments,
        },
        SshDetail s => new SshDetail
        {
            Id = s.Id, Name = s.Name, FolderName = s.FolderName, Notes = s.Notes, CustomFields = s.CustomFields,
            Created = s.Created, Edited = s.Edited, IsDeleted = s.IsDeleted, Favorite = s.Favorite, Reprompt = s.Reprompt,
            PublicKey = s.PublicKey, PrivateKey = s.PrivateKey, Fingerprint = s.Fingerprint,
            Attachments = attachments,
        },
        _ => detail,
    };
}
