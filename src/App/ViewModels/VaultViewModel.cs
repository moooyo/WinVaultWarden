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
    public bool HasSelectionForMove => IsSelectionMode && _selectedIds.Count > 0;

    [ObservableProperty]
    public partial CipherListItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial CipherDetail? Detail { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

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

    public bool HasSelection => Detail is not null;
    public bool NoSelection => Detail is null;
    public string? SelectedFilterTag => TagForFilter(SelectedFilter);
    public bool IsSelectedItemDeleted => SelectedItem?.IsDeleted == true;
    public bool IsFolderFilterSelected => SelectedFilter?.Kind == FilterKind.Folder;

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

    public VaultViewModel(IVaultUiService service, IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
        foreach (var it in service.GetItems()) Items.Add(it);
        foreach (var f in service.GetFilters()) Filters.Add(f);
        SelectedFilter = Filters.FirstOrDefault();
        ApplyFilter();
    }

    partial void OnSelectedItemChanged(CipherListItem? value)
    {
        Detail = value is null ? null : _service.GetDetail(value.Id);
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(IsSelectedItemDeleted));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

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

        IEnumerable<CipherListItem> source = SelectedFilter?.Kind switch
        {
            FilterKind.Favorites => Items.Where(i => i.Favorite && !i.IsDeleted),
            FilterKind.Trash     => Items.Where(i => i.IsDeleted),
            FilterKind.Type      => Items.Where(i => i.Kind == SelectedFilter.TypeFilter && !i.IsDeleted),
            FilterKind.Folder    => Items.Where(i => i.FolderId == SelectedFilter.FolderId && !i.IsDeleted),
            _                    => Items.Where(i => !i.IsDeleted), // AllItems 及未选中
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
            source = source.Where(i => i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                                    || i.Subtitle.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var i in source) FilteredItems.Add(i);
        RebuildGroups();
        OnPropertyChanged(nameof(HasNoItems));
        OnPropertyChanged(nameof(NoResults));
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
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
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

    [RelayCommand]
    private void Add() => BeginAdd(VaultItemKind.Login);

    public void ToggleSelection(string id)
    {
        if (!_selectedIds.Remove(id))
            _selectedIds.Add(id);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelectionForMove));
    }

    public bool IsSelected(string id) => _selectedIds.Contains(id);

    private void ClearSelection()
    {
        _selectedIds.Clear();
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelectionForMove));
    }

    partial void OnIsSelectionModeChanged(bool value)
    {
        if (!value)
            ClearSelection(); // ClearSelection 已通知 HasSelectionForMove
        else
            OnPropertyChanged(nameof(HasSelectionForMove));
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
        OnPropertyChanged(nameof(HasSelectionForMove));
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
}
