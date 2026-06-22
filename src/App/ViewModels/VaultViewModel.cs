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

    public VaultViewModel(IVaultUiService service, IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
        foreach (var it in service.GetItems()) Items.Add(it);
        foreach (var f in service.GetFilters()) Filters.Add(f);
        ApplyFilter();
    }

    partial void OnSelectedItemChanged(CipherListItem? value)
        => Detail = value is null ? null : _service.GetDetail(value.Id);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredItems.Clear();
        var q = string.IsNullOrWhiteSpace(SearchText)
            ? Items
            : Items.Where(i => i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                            || i.Subtitle.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        foreach (var i in q) FilteredItems.Add(i);
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
