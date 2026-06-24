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

    public ObservableCollection<CipherListItem> Items { get; } = new();
    public ObservableCollection<FilterNode> Filters { get; } = new();
    public ObservableCollection<CipherListItem> FilteredItems { get; } = new();

    [ObservableProperty] private CipherListItem? _selectedItem;
    [ObservableProperty] private CipherDetail? _detail;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FilterNode? _selectedFilter;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial CipherEditorDraft? EditorDraft { get; set; }

    [ObservableProperty]
    public partial string EditorError { get; set; } = string.Empty;

    public bool HasSelection => Detail is not null;
    public bool NoSelection => Detail is null;
    public string EditorTitle => EditorDraft?.Type switch
    {
        VaultItemKind.Login => "新增登录",
        VaultItemKind.Card => "新增支付卡",
        VaultItemKind.Identity => "新增身份",
        VaultItemKind.Note => "新增笔记",
        VaultItemKind.Ssh => "新增 SSH 密钥",
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

    partial void OnSelectedFilterChanged(FilterNode? value) => ApplyFilter();

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
    }

    public void BeginAdd(VaultItemKind type)
    {
        EditorDraft = CipherEditorDraft.CreateDefault(type);
        EditorError = string.Empty;
        IsEditing = true;
        SelectedItem = null;
        Detail = null;
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
        OnPropertyChanged(nameof(EditorTitle));
    }

    public void CancelEdit()
    {
        IsEditing = false;
        EditorDraft = null;
        EditorError = string.Empty;
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

    [RelayCommand]
    private void Sync() { /* mock:占位,真实同步后续接入 */ }

    [RelayCommand]
    private void Add() => BeginAdd(VaultItemKind.Login);

    [RelayCommand]
    private void Copy(string? value)
    {
        if (!string.IsNullOrEmpty(value)) _clipboard?.SetText(value);
    }
}
