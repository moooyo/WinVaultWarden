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

    public bool HasSelection => Detail is not null;
    public bool NoSelection => Detail is null;

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

    [RelayCommand]
    private void Sync() { /* mock:占位,真实同步后续接入 */ }

    [RelayCommand]
    private void Add() { /* mock:新增表单后续 */ }

    [RelayCommand]
    private void Copy(string? value)
    {
        if (!string.IsNullOrEmpty(value)) _clipboard?.SetText(value);
    }
}
